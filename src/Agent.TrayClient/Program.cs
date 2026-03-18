// Program.cs (WinForms + WPF tray application)
using Agent.TrayClient;
using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(true, "MonAppTrayClient", out bool createdNew);
        if (!createdNew) return;

        // Initialiser l'Application WPF avant de lancer la boucle WinForms.
        // Sans cette ligne, les fenêtres WPF n'ont pas accès aux ressources/thèmes WPF.
        // ShutdownMode.OnExplicitShutdown empêche WPF d'arrêter le process tout seul.
        _ = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
        };

        Application.Run(new MyTrayContext());
    }
}

public class MyTrayContext : ApplicationContext
{
    private readonly NotifyIcon  _trayIcon;
    private readonly CancellationTokenSource _cts = new();
    private readonly HubConnection?  _hubConnection;
    private readonly HotkeyManager   _hotkey;
    private readonly SearchService   _searchService;
    // Control caché pour invoquer sur le thread UI (SynchronizationContext peut être null
    // dans un tray app sans fenêtre principale avant Application.Run)
    private readonly Control _uiInvoker = new();
    private System.Drawing.Icon? _currentIcon;
    // Annulé à chaque changement réseau pour déclencher une reconnexion immédiate
    private CancellationTokenSource _networkChangeCts = new();
    // Fenêtre palette — une seule instance à la fois
    private System.Windows.Window? _palette;

    public MyTrayContext()
    {
        // Forcer la création du handle Win32 pour que BeginInvoke soit disponible
        // avant que le message loop soit démarré
        _ = _uiInvoker.Handle;

        // ── Icône tray ───────────────────────────────────────────────────────
        _trayIcon = new NotifyIcon()
        {
            Visible = true,
            Text    = "Mon Service — Déconnecté"
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Recherche  (Ctrl+Win+Alt+Espace)", null, (_, _) => ShowPalette());
        menu.Items.Add("Ouvrir le portail", null,
            (_, _) => OpenBrowser(ReadConfig("Agent:PortalUrl") ?? "https://mon-portail"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => ExitApp());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowPalette();

        // Icône initiale : rouge (pas encore connecté)
        SetIconAsync(ConnectionStatus.Disconnected);

        // ── Raccourci global ─────────────────────────────────────────────────
        _hotkey = new HotkeyManager();
        _hotkey.Pressed += (_, _) => ShowPalette();

        // ── Service de recherche ─────────────────────────────────────────────
        _searchService = BuildSearchService();

        // ── SignalR ──────────────────────────────────────────────────────────
        string? hubUrl = ReadConfig("Agent:HubUrl");
        if (hubUrl is not null)
        {
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(hubUrl, options =>
                {
                    // Envoie automatiquement les credentials Windows de l'utilisateur courant
                    options.UseDefaultCredentials = true;
                })
                .Build();

            _hubConnection.On<string>("OpenUrl", url => OpenBrowser(url));

            // Mettre le point rouge dès que SignalR coupe
            _hubConnection.Closed += _ =>
            {
                SetIconAsync(ConnectionStatus.Disconnected);
                return Task.CompletedTask;
            };

            // Reconnexion immédiate dès que le réseau change (VPN up, wifi, etc.)
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkChanged;

            Task.Run(() => ConnectSignalRAsync(_cts.Token));
        }
    }

    // ── Palette de recherche ─────────────────────────────────────────────────

    private void ShowPalette()
    {
        // Si la fenêtre est déjà ouverte, l'amener au premier plan
        if (_palette is { IsVisible: true })
        {
            _palette.Activate();
            return;
        }

        _palette = new PaletteWindow(_searchService);
        _palette.Closed += (_, _) => _palette = null;
        _palette.Show();
    }

    private void ExitApp()
    {
        _cts.Cancel();
        _hotkey.Dispose();
        _trayIcon.Visible = false;
        System.Windows.Application.Current?.Shutdown();
        Application.Exit();
    }

    // ── Construction du service de recherche ─────────────────────────────────

    private SearchService BuildSearchService()
    {
        var sources    = new List<ISearchSource>();
        var gedUrl     = ReadConfig("Search:GedUrl");
        var oamUrl     = ReadConfig("Search:OamUrl");

        if (!string.IsNullOrWhiteSpace(gedUrl)) sources.Add(new GedSearchSource(gedUrl!));
        if (!string.IsNullOrWhiteSpace(oamUrl)) sources.Add(new OamSearchSource(oamUrl!));

        int timeoutSec = int.TryParse(ReadConfig("Search:TimeoutSeconds"), out var t) ? t : 2;
        int cacheSec   = int.TryParse(ReadConfig("Search:CacheSeconds"),   out var c) ? c : 30;

        return new SearchService(
            sources,
            TimeSpan.FromSeconds(timeoutSec),
            TimeSpan.FromSeconds(cacheSec));
    }

    // ── Connexion SignalR ────────────────────────────────────────────────────

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        // VPN up, changement wifi, etc. — annule l'attente en cours pour retry immédiat
        var old = Interlocked.Exchange(ref _networkChangeCts, new CancellationTokenSource());
        try { old.Cancel(); } finally { old.Dispose(); }
    }

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),   // plafond — retry toutes les 5 min indéfiniment
    ];

    private async Task ConnectSignalRAsync(CancellationToken token)
    {
        int attempt = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_hubConnection!.State == HubConnectionState.Disconnected)
                {
                    await _hubConnection.StartAsync(token);
                    await _hubConnection.InvokeAsync("RegisterUser", Environment.MachineName, token);
                    SetIconAsync(ConnectionStatus.Connected);
                    attempt = 0; // réinitialise le backoff après une connexion réussie
                }

                await Task.Delay(5_000, token);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                await _hubConnection!.StopAsync(CancellationToken.None);
                SetIconAsync(ConnectionStatus.Disconnected);

                // Backoff exponentiel plafonné — interruptible par un changement réseau
                var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)];
                attempt++;

                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(
                    token, _networkChangeCts.Token);
                try
                {
                    await Task.Delay(delay, delayCts.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // Changement réseau détecté — on retente immédiatement
                    attempt = 0;
                }
            }
        }
    }

    // ── Mise à jour de l'icône ───────────────────────────────────────────────

    private void SetIconAsync(ConnectionStatus status)
    {
        _ = Task.Run(async () =>
        {
            var newIcon = await TrayIconBuilder.BuildAsync(status);

            _uiInvoker.BeginInvoke(() =>
            {
                var old = _currentIcon;
                _currentIcon   = newIcon;
                _trayIcon.Icon = newIcon;
                _trayIcon.Text = status == ConnectionStatus.Connected
                    ? "Mon Service — Connecté"
                    : "Mon Service — Déconnecté";
                old?.Dispose();
            });
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string? ReadConfig(string key)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path)) return null;
            JsonNode? node = JsonNode.Parse(File.ReadAllText(path));
            foreach (var part in key.Split(':'))
                node = node?[part];
            return node?.GetValue<string>();
        }
        catch { return null; }
    }

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { }
    }
}
