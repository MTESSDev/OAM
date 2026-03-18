#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Gestion complete du service Windows Agent.Service (OAM)
.DESCRIPTION
    Build, installation, demarrage, arret, suppression et consultation des logs.
#>
param(
    [ValidateSet("install","build","start","stop","reinstall","uninstall","status","logs","")]
    [string]$Action = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Configuration ──────────────────────────────────────────────────────────
$ServiceName        = "MonServiceSecure"
$ServiceDesc        = "Agent OAM - Surveillance et mise a jour"
$ServiceProjectPath = Join-Path $PSScriptRoot "src\Agent.Service"
$TrayProjectPath    = Join-Path $PSScriptRoot "src\Agent.TrayClient"
$PublishDir         = Join-Path $PSScriptRoot ".publish\Agent.Service"
$TrayPublishDir     = Join-Path $PublishDir "tray"
$ExePath            = Join-Path $PublishDir "Agent.Service.exe"
$TrayExePath        = Join-Path $TrayPublishDir "Agent.TrayClient.exe"


# ── Helpers ────────────────────────────────────────────────────────────────
function Write-Header($text) {
    Write-Host ""
    Write-Host "=== $text ===" -ForegroundColor Cyan
}

function Write-OK($text)  { Write-Host "[OK]  $text" -ForegroundColor Green  }
function Write-ERR($text) { Write-Host "[ERR] $text" -ForegroundColor Red    }
function Write-INF($text) { Write-Host "[INF] $text" -ForegroundColor Yellow }

function Get-ServiceStatus {
    return Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
}

# ── Actions ────────────────────────────────────────────────────────────────

function Invoke-Build {
    Write-Header "Build & Publish"
    Write-INF "Publication vers : $PublishDir"

    if (Test-Path $PublishDir) {
        Remove-Item $PublishDir -Recurse -Force
    }

    # 1. Publier Agent.Service
    Write-INF "Publication Agent.Service..."
    dotnet publish $ServiceProjectPath `
        --configuration Release `
        --output $PublishDir `
        --self-contained false

    if ($LASTEXITCODE -ne 0) {
        Write-ERR "Publication Agent.Service echouee (code $LASTEXITCODE)."
        exit 1
    }

    # 2. Publier Agent.TrayClient dans un sous-dossier tray\
    # Evite que son appsettings.json ecrase celui du Service
    Write-INF "Publication Agent.TrayClient..."
    dotnet publish $TrayProjectPath `
        --configuration Release `
        --output $TrayPublishDir `
        --self-contained false

    if ($LASTEXITCODE -ne 0) {
        Write-ERR "Publication Agent.TrayClient echouee (code $LASTEXITCODE)."
        exit 1
    }

    if (-not (Test-Path $ExePath)) {
        Write-ERR "Agent.Service.exe introuvable apres publication : $ExePath"
        exit 1
    }

    if (-not (Test-Path $TrayExePath)) {
        Write-ERR "Agent.TrayClient.exe introuvable apres publication : $TrayExePath"
        exit 1
    }

    # 3. Mettre a jour TrayClientPath dans l'appsettings.json publie du Service
    $appSettingsPath = Join-Path $PublishDir "appsettings.json"
    $json = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
    $json.Agent.TrayClientPath = $TrayExePath
    $json | ConvertTo-Json -Depth 5 | Set-Content $appSettingsPath -Encoding UTF8
    Write-INF "TrayClientPath mis a jour : $TrayExePath"

    Write-OK "Publication reussie."
    Write-INF "  Service    : $PublishDir"
    Write-INF "  TrayClient : $TrayPublishDir"
}

function Invoke-Install {
    Write-Header "Installation du service"

    $svc = Get-ServiceStatus
    if ($svc) {
        Write-INF "Le service existe deja (etat : $($svc.Status)). Utilise 'reinstall' pour forcer."
        return
    }

    if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
        New-EventLog -LogName Application -Source $ServiceName
        Write-OK "Source EventLog '$ServiceName' creee."
    }

    New-Service `
        -Name           $ServiceName `
        -BinaryPathName $ExePath `
        -DisplayName    $ServiceName `
        -Description    $ServiceDesc `
        -StartupType    Automatic

    # Forcer LocalSystem : requis pour WTSQueryUserToken (SE_TCB_NAME)
    # sans quoi le service ne peut pas lancer de processus dans les sessions utilisateur
    sc.exe config $ServiceName obj= "LocalSystem" | Out-Null

    Write-OK "Service '$ServiceName' installe (compte : LocalSystem)."
}

function Invoke-Start {
    Write-Header "Demarrage du service"
    $svc = Get-ServiceStatus
    if (-not $svc) { Write-ERR "Service non installe."; return }

    if ($svc.Status -eq "Running") {
        Write-INF "Le service est deja en cours d'execution."
        return
    }

    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 2

    $svc = Get-ServiceStatus
    if ($svc.Status -eq "Running") {
        Write-OK "Service demarre avec succes."
    } else {
        Write-ERR "Le service n'a pas demarre (etat : $($svc.Status)). Consulte les logs."
    }
}

function Invoke-Stop {
    Write-Header "Arret du service"
    $svc = Get-ServiceStatus
    if (-not $svc) { Write-INF "Service non installe."; return }

    if ($svc.Status -eq "Stopped") {
        Write-INF "Le service est deja arrete."
        return
    }

    Stop-Service -Name $ServiceName -Force
    Write-OK "Service arrete."
}

function Invoke-Uninstall {
    Write-Header "Desinstallation du service"
    $svc = Get-ServiceStatus

    if ($svc) {
        if ($svc.Status -ne "Stopped") {
            Write-INF "Arret du service avant suppression..."
            Stop-Service -Name $ServiceName -Force
            Start-Sleep -Seconds 2
        }
        sc.exe delete $ServiceName | Out-Null
        Write-OK "Service '$ServiceName' supprime."
    } else {
        Write-INF "Le service n'existe pas, rien a supprimer."
    }
}

function Invoke-Status {
    Write-Header "Statut du service"
    $svc = Get-ServiceStatus
    if (-not $svc) {
        Write-INF "Service '$ServiceName' : NON INSTALLE"
        return
    }

    $color = switch ($svc.Status) {
        "Running" { "Green"  }
        "Stopped" { "Red"    }
        default   { "Yellow" }
    }
    Write-Host "  Nom      : $($svc.Name)"       -ForegroundColor White
    Write-Host "  Etat     : $($svc.Status)"      -ForegroundColor $color
    Write-Host "  Demarrage: $($svc.StartType)"   -ForegroundColor White
    Write-Host "  Exe      : $ExePath"            -ForegroundColor White
}

function Invoke-Logs {
    param([int]$Last = 50)
    Write-Header "Derniers $Last evenements EventLog (source : $ServiceName)"

    $entries = Get-EventLog -LogName Application -Source $ServiceName `
                            -Newest $Last -ErrorAction SilentlyContinue

    if (-not $entries) {
        Write-INF "Aucun evenement trouve pour la source '$ServiceName'."
        return
    }

    foreach ($e in $entries) {
        $color = switch ($e.EntryType) {
            "Error"       { "Red"    }
            "Warning"     { "Yellow" }
            "Information" { "Gray"   }
            default       { "White"  }
        }
        Write-Host ("[$($e.TimeGenerated)] [{0,-11}] {1}" -f $e.EntryType, $e.Message) -ForegroundColor $color
    }
}

function Invoke-Reinstall {
    Invoke-Build
    Invoke-Uninstall
    Invoke-Install
    Invoke-Start
    Invoke-Status
}

# ── Menu ───────────────────────────────────────────────────────────────────
function Show-Menu {
    Write-Host ""
    Write-Host "--------------------------------------------" -ForegroundColor Cyan
    Write-Host "   Agent.Service - Gestion locale           " -ForegroundColor Cyan
    Write-Host "--------------------------------------------" -ForegroundColor Cyan
    Write-Host "  1. Build + Install + Start (complet)"
    Write-Host "  2. Build seulement"
    Write-Host "  3. Installer"
    Write-Host "  4. Demarrer"
    Write-Host "  5. Arreter"
    Write-Host "  6. Reinstaller (rebuild + restart)"
    Write-Host "  7. Desinstaller"
    Write-Host "  8. Statut"
    Write-Host "  9. Voir les logs (50 derniers)"
    Write-Host "  Q. Quitter"
    Write-Host "--------------------------------------------" -ForegroundColor Cyan
    Write-Host ""
}

# ── Point d'entree ─────────────────────────────────────────────────────────
switch ($Action.ToLower()) {
    "build"     { Invoke-Build }
    "install"   { Invoke-Build; Invoke-Install; Invoke-Start; Invoke-Status }
    "start"     { Invoke-Start }
    "stop"      { Invoke-Stop }
    "reinstall" { Invoke-Reinstall }
    "uninstall" { Invoke-Uninstall }
    "status"    { Invoke-Status }
    "logs"      { Invoke-Logs }
    default {
        do {
            Show-Menu
            $choice = Read-Host "Choix"
            switch ($choice) {
                "1" { Invoke-Build; Invoke-Install; Invoke-Start; Invoke-Status }
                "2" { Invoke-Build }
                "3" { Invoke-Install }
                "4" { Invoke-Start }
                "5" { Invoke-Stop }
                "6" { Invoke-Reinstall }
                "7" { Invoke-Uninstall }
                "8" { Invoke-Status }
                "9" { Invoke-Logs }
                "Q" { Write-Host "Au revoir." -ForegroundColor Cyan }
                "q" { Write-Host "Au revoir." -ForegroundColor Cyan }
                default { Write-INF "Choix invalide." }
            }
        } while ($choice -ne "Q" -and $choice -ne "q")
    }
}
