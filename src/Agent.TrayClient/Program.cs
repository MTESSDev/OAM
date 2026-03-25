// Program.cs (WinForms)
using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
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

#if TEST_MODE
        using var mutex = new Mutex(true, "AgentOAMTray-Test", out bool createdNew);
#else
        using var mutex = new Mutex(true, "AgentOAMTray", out bool createdNew);
#endif
        if (!createdNew) return;

        Application.Run(new MyTrayContext());
    }
}

public class MyTrayContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly CancellationTokenSource _cts = new();
    private readonly HubConnection? _hubConnection;
    // Control caché pour invoquer sur le thread UI — plus fiable que SynchronizationContext
    // dans un tray app sans fenêtre principale (Current peut être null avant Application.Run)
    private readonly Control _uiInvoker = new();
    private System.Drawing.Icon? _currentIcon;
    // Annulé à chaque changement réseau pour déclencher une reconnexion immédiate
    private CancellationTokenSource _networkChangeCts = new();
#if TEST_MODE
    private readonly string _envName;
#endif

    public MyTrayContext()
    {
        // Forcer la création du handle Win32 pour que BeginInvoke soit disponible
        // avant que le message loop soit démarré
        _ = _uiInvoker.Handle;

        _trayIcon = new NotifyIcon() { Visible = true, Text = "Agent OAM" };

        var menu = new ContextMenuStrip();

#if TEST_MODE
        _envName        = ReadConfigValue("Agent", "EnvironmentName") ?? "Test";
        string userName = $@"{Environment.UserDomainName}\{Environment.UserName}";

        var lblEnv  = new ToolStripMenuItem(_envName)         { Enabled = false };
        var lblUser = new ToolStripMenuItem($"[{userName}]")  { Enabled = false };
        menu.Items.Add(lblEnv);
        menu.Items.Add(lblUser);
        menu.Items.Add(new ToolStripSeparator());
#endif

        menu.Items.Add("Ouvrir le portail", null, (s, e) => OpenBrowser("https://mon-portail"));

#if TEST_MODE
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (s, e) => { _cts.Cancel(); Application.Exit(); });
#endif

        _trayIcon.ContextMenuStrip = menu;

        // Icône initiale : déconnecté
        SetIconAsync(ConnectionStatus.Disconnected);

        string? hubUrl = ReadHubUrl();
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

#if TEST_MODE
        Task.Run(() => CheckTestVersionAsync(_cts.Token));
#endif
        }

        //EnsureStartup();
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
#if TEST_MODE
                    ? $"Agent OAM [{_envName}] - Connecté"
                    : $"Agent OAM [{_envName}] - Déconnecté";
#else
                    ? "Agent OAM - Connecté"
                    : "Agent OAM - Déconnecté";
#endif
                old?.Dispose();
            });
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

#if TEST_MODE
    private static readonly HttpClient _http = new(new HttpClientHandler { UseDefaultCredentials = true });

    private async Task CheckTestVersionAsync(CancellationToken token)
    {
        try
        {
            string? checkUrl      = ReadConfigValue("Agent", "TestCheckUrl");
            string? updatePageUrl = ReadConfigValue("Agent", "TestUpdatePageUrl");

            if (string.IsNullOrEmpty(checkUrl)) return;

            string? exePath = Environment.ProcessPath;
            if (exePath is null || !File.Exists(exePath)) return;

            string localHash;
            using (var stream = File.OpenRead(exePath))
                localHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

            HttpResponseMessage response;
            try { response = await _http.GetAsync(checkUrl, token); }
            catch { return; }

            if (!response.IsSuccessStatusCode) return;

            string json        = await response.Content.ReadAsStringAsync(token);
            string? serverHash = JsonNode.Parse(json)?["hash"]?.GetValue<string>();

            if (serverHash is null || serverHash == localHash) return;

            _uiInvoker.BeginInvoke(() =>
            {
                using var form = new TestUpdateForm(_envName, updatePageUrl ?? checkUrl);
                form.ShowDialog();
                _cts.Cancel();
                Application.Exit();
            });
        }
        catch (OperationCanceledException) { }
        catch { }
    }
#endif

    private static string? ReadHubUrl() => ReadConfigValue("Agent", "HubUrl");

    private static string? ReadConfigValue(string section, string key)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(File.ReadAllText(path))?[section]?[key]?.GetValue<string>();
        }
        catch { return null; }
    }

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { }
    }

    private static void EnsureStartup()
    {
        const string registryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string appName     = "AgentOAMTray";
        string exePath = Assembly.GetExecutingAssembly().Location
            .Replace(".dll", ".exe", StringComparison.OrdinalIgnoreCase);

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryKey, writable: true);
        if (key?.GetValue(appName) as string != exePath)
            key?.SetValue(appName, exePath);
    }
}
