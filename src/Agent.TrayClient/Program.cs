// Program.cs (WinForms)
// Configurer le projet en OutputType: WinExe
using Agent.Shared;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // S'assurer qu'une seule instance tourne
        using var mutex = new Mutex(true, "MonAppTrayClient", out bool createdNew);
        if (!createdNew) return;

        Application.Run(new MyTrayContext());
    }
}

public class MyTrayContext : ApplicationContext
{
    private NotifyIcon _trayIcon;
    private CancellationTokenSource _cts = new();

    public MyTrayContext()
    {
        _trayIcon = new NotifyIcon()
        {
            Icon = SystemIcons.Application, // Mettre ta propre ic�ne
            Visible = true,
            Text = "Mon Service Sécurisé"
        };

        // Menu contextuel — pas de "Quitter" : le Service gère le cycle de vie
        var menu = new ContextMenuStrip();
        menu.Items.Add("Ouvrir le portail", null, (s, e) => OpenBrowser("https://mon-portail"));
        _trayIcon.ContextMenuStrip = menu;

        // D�marrer l'�coute du service en arri�re-plan
        Task.Run(() => ListenToService(_cts.Token));

        // S'assurer du d�marrage auto (Registry)
        EnsureStartup();
    }

    private async Task ListenToService(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", AppConstants.PipeName, PipeDirection.In);
                await client.ConnectAsync(token);

                using var reader = new BinaryReader(client);
                while (client.IsConnected && !token.IsCancellationRequested)
                {
                    // Lecture bloquante, attend un ordre du service
                    byte command = reader.ReadByte();

                    if (command == AppConstants.CommandOpenUrl)
                    {
                        string url = reader.ReadString();
                        // IMPORTANT : Ex�cuter sur le thread UI ou ThreadPool
                        OpenBrowser(url);
                    }
                }
            }
            catch
            {
                // Si le service n'est pas l� ou red�marre (update), on attend un peu
                await Task.Delay(2000, token);
            }
        }
    }

    private void OpenBrowser(string url)
    {
        try
        {
            // M�thode la plus rapide et compatible .NET Core pour ouvrir l'URL par d�faut
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex) { /* Log */ }
    }

    private static void EnsureStartup()
    {
        const string registryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string appName = "MonServiceSecureTray";
        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location
            .Replace(".dll", ".exe", StringComparison.OrdinalIgnoreCase);

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryKey, writable: true);
        if (key?.GetValue(appName) as string != exePath)
            key?.SetValue(appName, exePath);
    }

    // Exit() supprimé volontairement : l'utilisateur ne peut pas fermer le tray.
    // Le Service (SYSTEM) gère le cycle de vie via TrayProcessWatcher.
}