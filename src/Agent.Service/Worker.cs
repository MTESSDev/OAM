// Worker.cs
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Agent.Service;

[SupportedOSPlatform("windows")]
public class MainWorker : BackgroundService
{
    private readonly ILogger<MainWorker> _logger;
    private readonly string _updateUrl;
    private readonly string _trayExePath;
    private readonly string _hashFilePath;
    private readonly HttpClient _http = new();

    public MainWorker(ILogger<MainWorker> logger, IConfiguration configuration)
    {
        _logger = logger;
        _updateUrl = configuration["Agent:UpdateUrl"]
            ?? throw new InvalidOperationException("Agent:UpdateUrl manquant dans la configuration.");
        var configuredPath = configuration["Agent:TrayClientPath"];
        _trayExePath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(AppContext.BaseDirectory, "tray", "Agent.TrayClient.exe")
            : configuredPath;
        _hashFilePath = Path.Combine(AppContext.BaseDirectory, "last-update.sha256");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Maintenir le TrayClient en vie dans toutes les sessions actives
        _ = Task.Run(() => TrayProcessWatcher.WatchAsync(_trayExePath, _logger, stoppingToken),
            stoppingToken);

        // 2. Vérification de mise à jour immédiate au démarrage
        await CheckAndRunUpdate(stoppingToken);

        // 3. Vérification quotidienne dans la fenêtre de nuit (1h00–6h00)
        // Délai aléatoire dans la fenêtre pour étaler la charge sur le serveur
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = DelayUntilNextNightWindow();
            _logger.LogInformation("Prochaine vérification de mise à jour dans {h}h{m:D2}.",
                (int)delay.TotalHours, delay.Minutes);
            await Task.Delay(delay, stoppingToken);
            await CheckAndRunUpdate(stoppingToken);
        }
    }

    // ── Mise à jour ──────────────────────────────────────────────────────────

    private string ReadStoredHash()
    {
        if (!File.Exists(_hashFilePath)) return string.Empty;
        return File.ReadAllText(_hashFilePath).Trim().ToLowerInvariant();
    }

    private async Task CheckAndRunUpdate(CancellationToken token)
    {
        try
        {
            // 1. Lire le hash local (celui du package actuellement installé)
            string storedHash = ReadStoredHash();
            _logger.LogInformation("Vérification des mises à jour (hash local : {Hash})...",
                string.IsNullOrEmpty(storedHash) ? "(aucun)" : storedHash);

            // 2. Interroger le serveur
            var httpResponse = await _http.GetAsync(_updateUrl, token);

            if (httpResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Aucun package de mise à jour disponible sur le serveur.");
                return;
            }

            httpResponse.EnsureSuccessStatusCode();

            var response = await httpResponse.Content.ReadFromJsonAsync<UpdateCheckResponse>(cancellationToken: token);

            if (response is null || string.IsNullOrWhiteSpace(response.Hash))
            {
                _logger.LogWarning("Réponse invalide du serveur de mise à jour.");
                return;
            }

            string serverHash = response.Hash.Trim().ToLowerInvariant();

            if (string.Equals(storedHash, serverHash, StringComparison.Ordinal))
            {
                _logger.LogInformation("Aucune mise à jour disponible (hash identique).");
                return;
            }

            _logger.LogInformation("Mise à jour détectée. Hash serveur : {Hash}", serverHash);

            // 3. Télécharger le ZIP
            string tempZip = Path.Combine(Path.GetTempPath(), "OAM-update.zip");
            _logger.LogInformation("Téléchargement depuis {Url}...", response.DownloadUrl);
            byte[] zipBytes = await _http.GetByteArrayAsync(response.DownloadUrl, token);
            await File.WriteAllBytesAsync(tempZip, zipBytes, token);

            // 4. Vérifier le hash SHA-256 du fichier téléchargé
            string actualHash = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
            if (!string.Equals(actualHash, serverHash, StringComparison.Ordinal))
            {
                _logger.LogError(
                    "Hash SHA-256 invalide (attendu : {Expected}, reçu : {Actual}). Abandon.",
                    serverHash, actualHash);
                File.Delete(tempZip);
                return;
            }

            // 5. Extraire le ZIP dans un dossier temp
            string extractDir = Path.Combine(Path.GetTempPath(), "OAM-update");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(tempZip, extractDir);
            File.Delete(tempZip);
            _logger.LogInformation("ZIP extrait dans {Dir}.", extractDir);

            // 6. Lancer Agent.Updater depuis un dossier temp pour éviter qu'il s'écrase lui-même
            string updaterSource = Path.Combine(AppContext.BaseDirectory, "Agent.Updater.exe");
            if (!File.Exists(updaterSource))
            {
                _logger.LogError("Agent.Updater.exe introuvable : {Path}. Mise à jour annulée.", updaterSource);
                return;
            }

            string backupDir  = Path.Combine(Path.GetTempPath(), "OAM-backup");
            string installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');

            // Copie de l'updater en temp : il tourne hors de installDir et peut donc écraser sa propre source
            // Agent.Updater est publié en single-file self-contained : un seul .exe suffit
            string updaterTempDir = Path.Combine(Path.GetTempPath(), "OAM-updater");
            Directory.CreateDirectory(updaterTempDir);
            File.Copy(updaterSource, Path.Combine(updaterTempDir, "Agent.Updater.exe"), overwrite: true);

            // cmd /c start lance le processus de façon détachée — il survit à l'arrêt du service parent
            string updaterExe = Path.Combine(updaterTempDir, "Agent.Updater.exe");
            string arguments  = $"\"AgentOAM\" \"{extractDir}\" \"{installDir}\" \"{backupDir}\" \"{serverHash}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName        = "cmd.exe",
                Arguments       = $"/c start \"\" \"{updaterExe}\" {arguments}",
                UseShellExecute = false,
                CreateNoWindow  = true, 
            });

            _logger.LogInformation("Agent.Updater lancé (détaché). Le service va s'arrêter et redémarrer.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la vérification/application de la mise à jour.");
        }
    }

    /// <summary>
    /// Calcule le délai jusqu'à un moment aléatoire dans la prochaine fenêtre de nuit (1h00–6h00).
    /// Le délai aléatoire étale la charge sur le serveur quand plusieurs postes vérifient en même temps.
    /// </summary>
    private static TimeSpan DelayUntilNextNightWindow()
    {
        const int windowStartHour = 1;
        const int windowEndHour   = 6;

        var now        = DateTime.Now;
        var windowMins = (windowEndHour - windowStartHour) * 60;
        var offset     = TimeSpan.FromMinutes(Random.Shared.Next(0, windowMins));
        var target     = now.Date.AddHours(windowStartHour).Add(offset);

        if (target <= now)
            target = target.AddDays(1);

        return target - now;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        foreach (var proc in Process.GetProcessesByName("Agent.TrayClient"))
        {
            try
            {
                proc.Kill();
                _logger.LogInformation("Agent.TrayClient (PID {Pid}) arrêté avec le service.", proc.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Impossible d'arrêter Agent.TrayClient (PID {Pid}).", proc.Id);
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}

// Réponse de GET /updates/check
file record UpdateCheckResponse(string Hash, string DownloadUrl);
