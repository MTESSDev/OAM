using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agent.Server.Features.Updates;

public static class UpdatesEndpoints
{
    private const string ZipFileName = "agent.zip";
    private static readonly JsonSerializerOptions JsonIndented = new() { WriteIndented = true };

    public static void Map(WebApplication app)
    {
        app.MapGet("/updates/check",              Check);
        app.MapGet("/updates/download/{filename}", Download);
        app.MapGet("/updates/side/check",          SideCheck);
        app.MapGet("/updates/side/download",      SideDownload);
        app.MapGet("/updates/side/installer",     SideInstaller);
    }

    private static IResult Check(HttpContext ctx)
    {
        string filePath = Path.Combine(AppContext.BaseDirectory, "updates", ZipFileName);

        if (!File.Exists(filePath))
            return Results.NotFound(new { error = "Aucune mise a jour disponible." });

        using var stream = File.OpenRead(filePath);
        string hash        = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        string downloadUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/updates/download/{ZipFileName}";

        return Results.Ok(new { hash, downloadUrl });
    }

    private static IResult SideCheck()
    {
        string filePath = Path.Combine(AppContext.BaseDirectory, "updates", "side", "Agent.TrayClient.exe");

        if (!File.Exists(filePath))
            return Results.NotFound(new { error = "Aucun build side disponible." });

        using var stream = File.OpenRead(filePath);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        return Results.Ok(new { hash });
    }

    private static IResult SideDownload(HttpContext ctx, IConfiguration config)
    {
        string exePath = Path.Combine(AppContext.BaseDirectory, "updates", "side", "Agent.TrayClient.exe");

        if (!File.Exists(exePath))
            return Results.NotFound(new { error = "Aucun build side disponible." });

        string baseUrl         = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        string hubUrl          = config["Side:HubUrl"]          ?? "";
        string environmentName = config["Side:EnvironmentName"] ?? "";
        string updatePageUrl   = config["Side:UpdatePageUrl"]   ?? $"{baseUrl}/side-update";
        string checkUrl        = $"{baseUrl}/updates/side/check";

        var appSettings = new
        {
            Agent = new
            {
                HubUrl            = hubUrl,
                EnvironmentName   = environmentName,
                SideCheckUrl      = checkUrl,
                SideUpdatePageUrl = updatePageUrl,
            }
        };

        string appSettingsJson = JsonSerializer.Serialize(appSettings, JsonIndented);

        string exeFileName = string.IsNullOrEmpty(environmentName)
            ? "Agent.TrayClient.exe"
            : $"Agent.TrayClient.{environmentName}.exe";

        var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var exeEntry = archive.CreateEntry(exeFileName, CompressionLevel.NoCompression);
            using (var entryStream = exeEntry.Open())
            using (var fileStream  = File.OpenRead(exePath))
                fileStream.CopyTo(entryStream);

            var settingsEntry = archive.CreateEntry("appsettings.json", CompressionLevel.Fastest);
            using var settingsStream = settingsEntry.Open();
            settingsStream.Write(Encoding.UTF8.GetBytes(appSettingsJson));
        }

        zipStream.Position = 0;
        return Results.File(zipStream, "application/zip", "AgentOAM-Side.zip");
    }

    private static IResult SideInstaller(HttpContext ctx, IConfiguration config)
    {
        string exePath = Path.Combine(AppContext.BaseDirectory, "updates", "side", "Agent.TrayClient.exe");
        if (!File.Exists(exePath))
            return Results.NotFound(new { error = "Aucun build side disponible." });

        string baseUrl         = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        string environmentName = config["Side:EnvironmentName"] ?? "";
        string downloadUrl     = $"{baseUrl}/updates/side/download";
        string installDir      = string.IsNullOrEmpty(environmentName)
            ? @"C:\ProgramData\OAM-Side"
            : $@"C:\ProgramData\OAM-Side\{environmentName}";
        string exeFileName     = string.IsNullOrEmpty(environmentName)
            ? "Agent.TrayClient.exe"
            : $"Agent.TrayClient.{environmentName}.exe";

        string script = $$"""
            # Installateur Agent OAM - mode Side ({{environmentName}})
            # Genere automatiquement par le serveur - ne pas modifier

            $ErrorActionPreference = 'Stop'
            $installDir  = '{{installDir}}'
            $downloadUrl = '{{downloadUrl}}'
            $exeName     = '{{exeFileName}}'

            Write-Host "Installation de l'Agent OAM ({{environmentName}})..." -ForegroundColor Cyan

            # Creer le repertoire si necessaire
            if (-not (Test-Path $installDir)) {
                New-Item -ItemType Directory -Path $installDir -Force | Out-Null
                Write-Host "[OK] Repertoire cree : $installDir"
            } else {
                Write-Host "[OK] Repertoire existant : $installDir"
            }

            # Arreter l'agent si en cours d'execution
            $proc = Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($exeName)) -ErrorAction SilentlyContinue
            if ($proc) {
                $proc | Stop-Process -Force
                Start-Sleep -Seconds 1
                Write-Host "[OK] Agent arrete."
            }

            # Telecharger le ZIP
            $zipPath = Join-Path $env:TEMP 'AgentOAM-Side.zip'
            Write-Host "Telechargement depuis $downloadUrl ..."
            $client = New-Object System.Net.WebClient
            $client.UseDefaultCredentials = $true
            $client.DownloadFile($downloadUrl, $zipPath)
            Write-Host "[OK] Telechargement termine."

            # Extraire dans le repertoire d'installation (Force = ecrase les fichiers existants)
            Expand-Archive -Path $zipPath -DestinationPath $installDir -Force
            Remove-Item $zipPath -Force
            Write-Host "[OK] Fichiers extraits dans $installDir"

            # Creer un raccourci sur le bureau
            $exePath      = Join-Path $installDir $exeName
            $shortcutPath = Join-Path $env:USERPROFILE "Desktop\Agent OAM - {{environmentName}}.lnk"
            $shell        = New-Object -ComObject WScript.Shell
            $shortcut     = $shell.CreateShortcut($shortcutPath)
            $shortcut.TargetPath       = $exePath
            $shortcut.WorkingDirectory = $installDir
            $shortcut.Description      = "Agent OAM - {{environmentName}}"
            $shortcut.Save()
            Write-Host "[OK] Raccourci cree sur le bureau."

            if (-not (Test-Path $exePath)) {
                Write-Host "[ERR] Executable introuvable : $exePath" -ForegroundColor Red
            }

            Write-Host ""
            Write-Host "Installation terminee. Lancez l'agent via le raccourci sur votre bureau." -ForegroundColor Green
            Start-Sleep -Seconds 2
            """;

        byte[] scriptBytes = Encoding.UTF8.GetBytes(script);
        string fileName    = string.IsNullOrEmpty(environmentName)
            ? "Install-AgentOAM.ps1"
            : $"Install-AgentOAM-{environmentName}.ps1";

        return Results.File(scriptBytes, "application/octet-stream", fileName);
    }

    private static IResult Download(string filename)
    {
        if (filename.Contains('/') || filename.Contains('\\') || filename.Contains(".."))
            return Results.BadRequest(new { error = "Nom de fichier invalide." });

        string filePath = Path.Combine(AppContext.BaseDirectory, "updates", filename);

        if (!File.Exists(filePath))
            return Results.NotFound(new { error = $"Fichier '{filename}' introuvable." });

        return Results.File(filePath, "application/zip", filename);
    }
}
