// Worker.cs
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Agent.Service;

[SupportedOSPlatform("windows")]
public partial class MainWorker : BackgroundService
{
    private readonly ILogger<MainWorker> _logger;
    private readonly HubConnection _hubConnection;
    private readonly string _updateUrl;
    private readonly string _trayExePath;

    // Annulé à chaque changement réseau pour déclencher une reconnexion immédiate
    private CancellationTokenSource _networkChangeCts = new();

    public MainWorker(ILogger<MainWorker> logger, IConfiguration configuration)
    {
        _logger = logger;
        _updateUrl = configuration["Agent:UpdateUrl"]
            ?? throw new InvalidOperationException("Agent:UpdateUrl manquant dans la configuration.");
        var configuredPath = configuration["Agent:TrayClientPath"];
        _trayExePath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(AppContext.BaseDirectory, "Agent.TrayClient.exe")
            : configuredPath;

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(configuration["Agent:HubUrl"]
                ?? throw new InvalidOperationException("Agent:HubUrl manquant dans la configuration."))
            .Build(); // Pas WithAutomaticReconnect : on gère nous-mêmes via Closed + réseau

        // OpenUrl est désormais géré directement par le TrayClient via sa propre connexion SignalR
        _hubConnection.On("CheckUpdate", async () => await CheckAndRunUpdate());

        _hubConnection.Closed += OnHubConnectionClosed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        try
        {
            // 1. Surveiller et maintenir le TrayClient en vie dans toutes les sessions actives
            _ = Task.Run(() => TrayProcessWatcher.WatchAsync(_trayExePath, _logger, stoppingToken),
                stoppingToken);

            // 2. Connexion SignalR pour la gestion machine (CheckUpdate, etc.)
            await ReconnectSignalRLoop(stoppingToken);

            // 3. Maintenance journalière
            using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await CheckAndRunUpdate();
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _networkChangeCts.Dispose();
        }
    }

    // ── Surveillance réseau ──────────────────────────────────────────────────

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        var old = Interlocked.Exchange(ref _networkChangeCts, new CancellationTokenSource());
        try { old.Cancel(); } finally { old.Dispose(); }

        _logger.LogInformation("Changement réseau détecté — tentative de reconnexion SignalR.");
    }

    // ── SignalR ──────────────────────────────────────────────────────────────

    private async Task OnHubConnectionClosed(Exception? ex)
    {
        if (ex is not null)
            _logger.LogWarning(ex, "SignalR déconnecté de façon inattendue.");
        else
            _logger.LogInformation("SignalR fermé proprement.");

        OnNetworkAddressChanged(null, EventArgs.Empty);
    }

    private async Task ReconnectSignalRLoop(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_hubConnection.State == HubConnectionState.Connected)
            {
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            try
            {
                await _hubConnection.StartAsync(stoppingToken);

                string version = System.Reflection.Assembly
                    .GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
                await _hubConnection.InvokeAsync("RegisterAgent",
                    Environment.MachineName, version, stoppingToken);

                _logger.LogInformation("SignalR connecté : machine {Machine} v{Version} enregistrée.",
                    Environment.MachineName, version);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconnexion SignalR échouée. Attente avant retry...");

                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken, _networkChangeCts.Token);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), delayCts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Réseau disponible — reconnexion SignalR immédiate.");
                }
            }
        }
    }

    // ── Mise à jour ──────────────────────────────────────────────────────────

    private async Task CheckAndRunUpdate()
    {
        // 1. Check version API
        // 2. Download ZIP to Temp
        // 3. Extract to TempDir
        // 4. Run Updater.exe
        LogCheckingForUpdates(_updateUrl);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Vérification des mises à jour via {UpdateUrl}...")]
    private partial void LogCheckingForUpdates(string updateUrl);
}
