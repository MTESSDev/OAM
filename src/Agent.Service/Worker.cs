// Worker.cs
using Agent.Shared;
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

        // 3. Vérification périodique toutes les heures
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await CheckAndRunUpdate(stoppingToken);
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
            var response = await _http.GetFromJsonAsync<UpdateCheckResponse>(_updateUrl, token);

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

            // 6. Lancer Agent.Updater (arrête le service, remplace les fichiers, redémarre)
            string updaterPath = Path.Combine(AppContext.BaseDirectory, "Agent.Updater.exe");
            if (!File.Exists(updaterPath))
            {
                _logger.LogError("Agent.Updater.exe introuvable : {Path}. Mise à jour annulée.", updaterPath);
                return;
            }

            string backupDir  = Path.Combine(Path.GetTempPath(), "OAM-backup");
            string installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');

            // Passer le nouveau hash en dernier argument : l'Updater l'écrit dans last-update.sha256
            Process.Start(new ProcessStartInfo
            {
                FileName        = updaterPath,
                Arguments       = $"\"{AppConstants.ServiceName}\" \"{extractDir}\" \"{installDir}\" \"{backupDir}\" \"{serverHash}\"",
                UseShellExecute = false,
                CreateNoWindow  = true
            });

            _logger.LogInformation("Agent.Updater lancé. Le service va s'arrêter et redémarrer.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la vérification/application de la mise à jour.");
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
