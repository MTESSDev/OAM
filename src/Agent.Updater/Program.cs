// Program.cs (Updater)
using System;
using System.IO;
using System.ServiceProcess;

// Args: [0]=ServiceName, [1]=SourceDir, [2]=InstallDir, [3]=BackupDir, [4]=NewHash (optional)
if (args.Length < 4)
{
    Console.Error.WriteLine("Usage: Agent.Updater <ServiceName> <SourceDir> <InstallDir> <BackupDir> [NewHash]");
    return;
}

string serviceName = args[0];
string sourceDir   = args[1];
string installDir  = args[2];
string backupDir   = args[3];
string newHash     = args.Length >= 5 ? args[4] : string.Empty;

try
{
    Console.WriteLine("Arret du service...");
    using var sc = new ServiceController(serviceName);
    if (sc.Status != ServiceControllerStatus.Stopped)
    {
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
    }

    Console.WriteLine("Creation du backup (failsafe)...");
    if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true);
    CopyDirectory(installDir, backupDir);

    Console.WriteLine("Application de la mise a jour...");
    CopyDirectory(sourceDir, installDir);

    // Enregistrer le hash du nouveau package pour que le service ne re-telecharge pas au demarrage
    if (!string.IsNullOrWhiteSpace(newHash))
        File.WriteAllText(Path.Combine(installDir, "last-update.sha256"), newHash);

    Console.WriteLine("Redemarrage du service...");
    sc.Refresh();
    sc.Start();
    Console.WriteLine("Mise a jour terminee avec succes.");
}
catch (Exception ex)
{
    Console.WriteLine($"ECHEC CRITIQUE: {ex.Message}. Restauration du backup...");
    try
    {
        CopyDirectory(backupDir, installDir);
        using var sc = new ServiceController(serviceName);
        sc.Start();
        Console.WriteLine("Backup restaure, service redémarre.");
    }
    catch (Exception restoreEx)
    {
        Console.Error.WriteLine($"Echec de la restauration : {restoreEx.Message}");
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
