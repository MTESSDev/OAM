// Program.cs (Updater)
using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

// Args: [0]=ServiceName, [1]=SourceDir, [2]=InstallDir, [3]=BackupDir
if (args.Length < 4)
{
    Console.Error.WriteLine("Usage: Agent.Updater <ServiceName> <SourceDir> <InstallDir> <BackupDir>");
    return;
}

string serviceName = args[0];
string sourceDir = args[1];
string installDir = args[2];
string backupDir = args[3];

try
{
    Console.WriteLine("Arr�t du service...");
    using var sc = new ServiceController(serviceName);
    if (sc.Status != ServiceControllerStatus.Stopped)
    {
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
    }

    Console.WriteLine("Cr�ation du backup (Failsafe)...");
    if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true);
    // Copie simple du dossier actuel vers backup
    CopyDirectory(installDir, backupDir);

    Console.WriteLine("Application de la mise � jour...");
    CopyDirectory(sourceDir, installDir);

    Console.WriteLine("Red�marrage du service...");
    sc.Start();
}
catch (Exception ex)
{
    // FAILSAFE : On restaure le backup si �a plante
    Console.WriteLine($"ECHEC CRITIQUE: {ex.Message}. Restauration...");
    try
    {
        CopyDirectory(backupDir, installDir);
        using var sc = new ServiceController(serviceName);
        sc.Start();
    }
    catch { /* Log fatal to disk */ }
}

// Helper simple
void CopyDirectory(string source, string dest)
{
    Directory.CreateDirectory(dest);
    foreach (var file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
    foreach (var dir in Directory.GetDirectories(source))
        CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
}