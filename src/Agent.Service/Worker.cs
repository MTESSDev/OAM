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
using System.Reflection;
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

    private async Task CheckAndRunUpdate(CancellationToken token)
    {
        string currentVersion = Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "1.0.0";

        try
        {
            // 1. Interroger le serveur
            _logger.LogInformation("Vérification des mises à jour (version courante : {Version})...", currentVersion);

            var response = await _http.GetFromJsonAsync<UpdateCheckResponse>(
                $"{_updateUrl}?version={currentVersion}", token);

            if (response?.HasUpdate != true || response.Update is null)
            {
                _logger.LogInformation("Aucune mise à jour disponible.");
                return;
            }

            _logger.LogInformation("Mise à jour disponible : {Old} → {New}.",
                currentVersion, response.Update.Version);

            // 2. Télécharger le ZIP
            string tempZip = Path.Combine(
                Path.GetTempPath(), $"OAM-update-{response.Update.Version}.zip");

            _logger.LogInformation("Téléchargement depuis {Url}...", response.Update.Url);
            byte[] zipBytes = await _http.GetByteArrayAsync(response.Update.Url, token);
            await File.WriteAllBytesAsync(tempZip, zipBytes, token);

            // 3. Vérifier le hash SHA-256
            string actualHash = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
            if (!string.Equals(actualHash, response.Update.Hash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "Hash SHA-256 invalide pour la mise à jour {Version} " +
                    "(attendu : {Expected}, reçu : {Actual}). Abandon.",
                    response.Update.Version, response.Update.Hash, actualHash);
                File.Delete(tempZip);
                return;
            }

            // 4. Extraire le ZIP dans un dossier temp
            string extractDir = Path.Combine(
                Path.GetTempPath(), $"OAM-update-{response.Update.Version}");

            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(tempZip, extractDir);
            File.Delete(tempZip);
            _logger.LogInformation("ZIP extrait dans {Dir}.", extractDir);

            // 5. Lancer Agent.Updater (arrête le service, remplace les fichiers, redémarre)
            string updaterPath = Path.Combine(AppContext.BaseDirectory, "Agent.Updater.exe");
            if (!File.Exists(updaterPath))
            {
                _logger.LogError("Agent.Updater.exe introuvable : {Path}. Mise à jour annulée.", updaterPath);
                return;
            }

            string backupDir = Path.Combine(
                Path.GetTempPath(), $"OAM-backup-{currentVersion}");
            string installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');

            Process.Start(new ProcessStartInfo
            {
                FileName         = updaterPath,
                Arguments        = $"\"{AppConstants.ServiceName}\" \"{extractDir}\" \"{installDir}\" \"{backupDir}\"",
                UseShellExecute  = false,
                CreateNoWindow   = true
            });

            _logger.LogInformation(
                "Agent.Updater lancé pour {Version}. Le service va s'arrêter et redémarrer.",
                response.Update.Version);
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
file record UpdateCheckResponse(bool HasUpdate, UpdateInfo? Update);
