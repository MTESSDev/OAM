// TrayProcessWatcher.cs
// Lance et surveille Agent.TrayClient.exe dans la session console active.
// Le Service tournant en SYSTEM (Session 0) utilise WTSQueryUserToken +
// CreateProcessAsUser pour injecter le processus dans la session de l'utilisateur.
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Agent.Service;

[SupportedOSPlatform("windows")]
internal static class TrayProcessWatcher
{
    // ── Win32 P/Invoke ───────────────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int ImpersonationLevel,
        int TokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken, string? lpApplicationName, string? lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars,
                    dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_DUPLICATE      = 0x0002;
    private const uint TOKEN_QUERY          = 0x0008;
    private const uint NORMAL_PRIORITY_CLASS = 0x0020;

    // ── API publique ─────────────────────────────────────────────────────────

    /// <summary>
    /// Boucle infinie : s'assure qu'une instance de TrayClient tourne toujours
    /// dans la session console. Si le processus se termine (même tué par
    /// l'utilisateur via le Gestionnaire des tâches), il est relancé après 2s.
    /// </summary>
    public static async Task WatchAsync(string trayExePath, ILogger logger, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Process? process = null;
            try
            {
                if (!File.Exists(trayExePath))
                {
                    logger.LogWarning("TrayClient introuvable : {Path}. Nouvelle tentative dans 10s.", trayExePath);
                    await Task.Delay(10000, token);
                    continue;
                }

                KillExistingInstances(trayExePath, logger);
                process = LaunchInUserSession(trayExePath, logger);
                if (process is null)
                {
                    // Aucune session active (ex. ouverture de session pas encore faite)
                    await Task.Delay(5000, token);
                    continue;
                }

                logger.LogInformation("TrayClient lancé (PID {Pid}).", process.Id);
                await process.WaitForExitAsync(token);
                logger.LogWarning("TrayClient arrêté (code {Code}). Redémarrage dans 2s...",
                    process.ExitCode);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Erreur surveillance TrayClient."); }
            finally { process?.Dispose(); }

            if (!token.IsCancellationRequested)
                await Task.Delay(2000, token);
        }
    }

    // ── Nettoyage des instances existantes ──────────────────────────────────

    private static void KillExistingInstances(string exePath, ILogger logger)
    {
        var processName = Path.GetFileNameWithoutExtension(exePath);
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try
            {
                logger.LogInformation("Instance TrayClient existante détectée (PID {Pid}), fermeture.", p.Id);
                p.Kill();
                p.WaitForExit(3000);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Impossible de tuer le PID {Pid}.", p.Id); }
            finally { p.Dispose(); }
        }
    }

    // ── Lancement dans la session utilisateur ───────────────────────────────

    private static Process? LaunchInUserSession(string exePath, ILogger logger)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            logger.LogWarning("Aucune session console active (WTSGetActiveConsoleSessionId).");
            return null;
        }

        if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
        {
            // WTSQueryUserToken exige SE_TCB_NAME (SYSTEM uniquement).
            // En debug / compte non-SYSTEM → fallback Process.Start dans la session courante.
            logger.LogWarning(
                "WTSQueryUserToken échoué (session {Id}, erreur Win32 {Err}). " +
                "Fallback Process.Start (non-SYSTEM / debug).",
                sessionId, Marshal.GetLastWin32Error());

            return Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            });
        }

        try
        {
            // DuplicateTokenEx : nécessaire pour obtenir un Primary Token utilisable
            if (!DuplicateTokenEx(
                    userToken,
                    TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY,
                    IntPtr.Zero,
                    ImpersonationLevel: 2,   // SecurityImpersonation
                    TokenType: 1,            // TokenPrimary
                    out IntPtr primaryToken))
            {
                logger.LogWarning("DuplicateTokenEx échoué (erreur Win32 {Err}).",
                    Marshal.GetLastWin32Error());
                return null;
            }

            try
            {
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                bool ok = CreateProcessAsUser(
                    primaryToken,
                    exePath, null,
                    IntPtr.Zero, IntPtr.Zero,
                    bInheritHandles: false,
                    NORMAL_PRIORITY_CLASS,
                    IntPtr.Zero,
                    Path.GetDirectoryName(exePath),
                    ref si, out var pi);

                if (!ok)
                {
                    logger.LogWarning("CreateProcessAsUser échoué (erreur Win32 {Err}).",
                        Marshal.GetLastWin32Error());
                    return null;
                }

                CloseHandle(pi.hThread);
                return Process.GetProcessById((int)pi.dwProcessId);
            }
            finally { CloseHandle(primaryToken); }
        }
        finally { CloseHandle(userToken); }
    }
}
