// Program.cs (Updater)
using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text.Json;

string logPath = Path.Combine(Path.GetTempPath(), "OAM-updater.log");
using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };

void Log(string msg)
{
    string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
    log.WriteLine(line);
    Console.WriteLine(line);
}

// Args: [0]=ServiceName, [1]=SourceDir, [2]=InstallDir, [3]=BackupDir, [4]=NewHash (optional)
if (args.Length < 4)
{
    Log("Usage: Agent.Updater <ServiceName> <SourceDir> <InstallDir> <BackupDir> [NewHash]");
    return;
}

string serviceName = args[0];
string sourceDir   = args[1];
string installDir  = args[2];
string backupDir   = args[3];
string newHash     = args.Length >= 5 ? args[4] : string.Empty;

Log($"Demarrage. Service={serviceName} Source={sourceDir} InstallDir={installDir}");

try
{
    Log("Arret du service...");
    using var sc = new ServiceController(serviceName);
    if (sc.Status != ServiceControllerStatus.Stopped)
    {
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
    }
    Log("Service arrete.");

    Log("Arret des processus TrayClient...");
    foreach (var p in System.Diagnostics.Process.GetProcessesByName("Agent.TrayClient"))
    {
        try { p.Kill(); p.WaitForExit(5000); } catch { }
    }
    Log("TrayClient arrete.");

    Log("Creation du backup (failsafe)...");
    if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true);
    CopyDirectory(installDir, backupDir);
    Log("Backup cree.");

    Log("Application de la mise a jour...");
    CopyDirectory(sourceDir, installDir);
    Log("Fichiers copies.");

    if (!string.IsNullOrWhiteSpace(newHash))
    {
        File.WriteAllText(Path.Combine(installDir, "last-update.sha256"), newHash);
        Log($"Hash enregistre : {newHash}");
    }

    // Appliquer DisplayName et Description depuis appsettings.json
    string appSettingsPath = Path.Combine(installDir, "appsettings.json");
    if (File.Exists(appSettingsPath))
    {
        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(appSettingsPath));
            if (root.TryGetProperty("Service", out var svc))
            {
                if (svc.TryGetProperty("DisplayName", out var dn) && !string.IsNullOrWhiteSpace(dn.GetString()))
                {
                    RunSc($"config \"{serviceName}\" DisplayName= \"{dn.GetString()}\"");
                    Log($"DisplayName mis a jour : {dn.GetString()}");
                }
                if (svc.TryGetProperty("Description", out var desc) && !string.IsNullOrWhiteSpace(desc.GetString()))
                {
                    RunSc($"description \"{serviceName}\" \"{desc.GetString()}\"");
                    Log($"Description mise a jour : {desc.GetString()}");
                }
            }
        }
        catch (Exception ex) { Log($"Avertissement : impossible de lire appsettings.json : {ex.Message}"); }
    }

    Log("Redemarrage du service...");
    sc.Refresh();
    sc.Start();
    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
    Log("Mise a jour terminee avec succes.");
}
catch (Exception ex)
{
    Log($"ECHEC CRITIQUE: {ex}");
    Log("Restauration du backup...");
    try
    {
        CopyDirectory(backupDir, installDir);
        using var sc = new ServiceController(serviceName);
        sc.Start();
        Log("Backup restaure, service redemarre.");
    }
    catch (Exception restoreEx)
    {
        Log($"Echec de la restauration : {restoreEx}");
    }
}

static void CopyDirectory(string source, string dest)
{
    Directory.CreateDirectory(dest);
    foreach (var file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
    foreach (var dir in Directory.GetDirectories(source))
        CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
}

static void RunSc(string arguments)
{
    using var p = Process.Start(new ProcessStartInfo
    {
        FileName        = "sc.exe",
        Arguments       = arguments,
        UseShellExecute = false,
        CreateNoWindow  = true
    });
    p?.WaitForExit();
}
