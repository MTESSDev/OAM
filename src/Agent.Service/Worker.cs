// Worker.cs
using Agent.Shared;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
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
    private NamedPipeServerStream? _pipeServer;

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

        _hubConnection.On<string>("OpenUrl", url => _ = BroadcastUrlToClient(url));
        _hubConnection.On("CheckUpdate", async () => await CheckAndRunUpdate());

        // Quand SignalR coupe, on relance immédiatement la boucle de reconnexion
        _hubConnection.Closed += OnHubConnectionClosed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        try
        {
            // 1. Surveiller et maintenir le TrayClient en vie dans la session utilisateur
            _ = Task.Run(() => TrayProcessWatcher.WatchAsync(_trayExePath, _logger, stoppingToken),
                stoppingToken);

            // 2. Démarrer le serveur IPC
            _ = Task.Run(() => StartIpcServer(stoppingToken), stoppingToken);

#if DEBUG
            _ = Task.Run(async () =>
            {
                _logger.LogInformation("DEBUG: Simulation ouverture URL dans 10s...");
                await Task.Delay(10000, stoppingToken);
                await BroadcastUrlToClient("https://www.google.ca");
            }, stoppingToken);
#else
            // 3. Connexion SignalR (boucle infinie, résiliente aux pannes réseau)
            await ReconnectSignalRLoop(stoppingToken);

            // 4. Maintenance journalière
            using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await CheckAndRunUpdate();
#endif
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
        // Réveille immédiatement la boucle de reconnexion SignalR en cours d'attente
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

        // La boucle de reconnexion sera reprise au prochain tick réseau ou immédiatement
        OnNetworkAddressChanged(null, EventArgs.Empty);
    }

#pragma warning disable IDE0051 // Utilisé dans le bloc #else (Release uniquement)
    /// <summary>
    /// Boucle de reconnexion infinie.
    /// - Retry immédiat si le réseau change (NetworkAddressChanged)
    /// - Sinon attend jusqu'à 10s avant de réessayer
    /// </summary>
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

                // S'enregistrer auprès du hub pour apparaître dans le registre serveur
                string version = System.Reflection.Assembly
                    .GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
                await _hubConnection.InvokeAsync("RegisterAgent",
                    Environment.MachineName, version, stoppingToken);

                _logger.LogInformation("SignalR connecté et agent enregistré ({Machine} v{Version}).",
                    Environment.MachineName, version);
                return; // Connexion réussie — on sort, le Closed event reprend la main si besoin
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconnexion SignalR échouée. Attente avant retry...");

                // Attend au plus 10s OU jusqu'au prochain événement réseau
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken, _networkChangeCts.Token);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), delayCts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Réseau disponible → on retente immédiatement (pas de délai)
                    _logger.LogInformation("Réseau disponible — reconnexion SignalR immédiate.");
                }
            }
        }
    }

#pragma warning restore IDE0051

    // ── IPC (Service → TrayClient) ───────────────────────────────────────────

    private async Task StartIpcServer(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Utiliser le SID BUILTIN\Users (S-1-5-32-545) résolu localement,
                // sans appel au contrôleur de domaine — évite l'erreur 1788.
                var builtinUsers = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                var pipeSecurity = new PipeSecurity();
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    builtinUsers, PipeAccessRights.ReadWrite, AccessControlType.Allow));

                _pipeServer = NamedPipeServerStreamAcl.Create(
                    AppConstants.PipeName,
                    PipeDirection.Out,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0, 0, pipeSecurity);

                await _pipeServer.WaitForConnectionAsync(token);
                _logger.LogInformation("TrayClient connecté au pipe IPC.");

                // Attendre la déconnexion du client avant de recréer le serveur
                while (_pipeServer.IsConnected && !token.IsCancellationRequested)
                    await Task.Delay(500, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erreur serveur IPC, redémarrage...");
                if (_pipeServer?.IsConnected == true) _pipeServer.Disconnect();
            }
        }
    }

    private async Task BroadcastUrlToClient(string url)
    {
        if (_pipeServer is { IsConnected: true })
        {
            try
            {
                using var writer = new BinaryWriter(_pipeServer, System.Text.Encoding.UTF8, leaveOpen: true);
                writer.Write(AppConstants.CommandOpenUrl);
                writer.Write(url);
                await _pipeServer.FlushAsync();
            }
            catch (Exception ex) { _logger.LogError(ex, "Erreur IPC"); }
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
