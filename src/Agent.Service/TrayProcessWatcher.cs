// TrayProcessWatcher.cs
// Enumère toutes les sessions graphiques actives et s'assure qu'une instance
// de TrayClient tourne dans chacune d'elles.
// IMPORTANT : nécessite que le service tourne en LocalSystem (SE_TCB_NAME).
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer, uint reserved, uint version,
        out IntPtr ppSessionInfo, out uint pCount);

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

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

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    // ── Structures ───────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint   SessionId;
        public IntPtr pWinStationName;
        public int    State; // WTSActive = 0
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int     cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public uint    dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars,
                       dwFillAttribute, dwFlags;
        public ushort  wShowWindow, cbReserved2;
        public IntPtr  lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint   dwProcessId, dwThreadId;
    }

    // ── Constantes ───────────────────────────────────────────────────────────

    private const int    WTSActive               = 0;
    private const uint   TOKEN_ASSIGN_PRIMARY    = 0x0001;
    private const uint   TOKEN_DUPLICATE         = 0x0002;
    private const uint   TOKEN_QUERY             = 0x0008;
    private const uint   NORMAL_PRIORITY_CLASS   = 0x0020;
    private const uint   CREATE_UNICODE_ENVIRONMENT = 0x0400;
    private const uint   STARTF_USESHOWWINDOW    = 0x0001;
    private const ushort SW_SHOWNORMAL           = 1;
    private static readonly IntPtr WTS_CURRENT_SERVER = IntPtr.Zero;

    // ── API publique ─────────────────────────────────────────────────────────

    /// <summary>
    /// Boucle principale : toutes les 5 s, s'assure qu'une instance de TrayClient
    /// tourne dans chaque session utilisateur graphique active.
    /// </summary>
    public static async Task WatchAsync(string trayExePath, ILogger logger, CancellationToken token)
    {
        string processName = Path.GetFileNameWithoutExtension(trayExePath);

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(trayExePath))
                {
                    logger.LogWarning("TrayClient introuvable : {Path}. Nouvelle tentative dans 10s.", trayExePath);
                    await Task.Delay(10_000, token);
                    continue;
                }

                foreach (uint sessionId in GetActiveUserSessions(logger))
                {
                    if (IsTrayRunningInSession(processName, sessionId))
                        continue;

                    bool launched = LaunchInSession(trayExePath, sessionId, logger);
                    if (launched)
                        logger.LogInformation("TrayClient lancé dans la session {SessionId}.", sessionId);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Erreur surveillance TrayClient."); }

            await Task.Delay(5_000, token);
        }
    }

    // ── Sessions actives ─────────────────────────────────────────────────────

    private static IEnumerable<uint> GetActiveUserSessions(ILogger logger)
    {
        if (!WTSEnumerateSessions(WTS_CURRENT_SERVER, 0, 1, out IntPtr pInfo, out uint count))
        {
            logger.LogWarning("WTSEnumerateSessions échoué (erreur Win32 {Err}).",
                Marshal.GetLastWin32Error());
            yield break;
        }

        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (uint i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(pInfo + (int)(i * size));

                // WTSActive (0) = session avec un utilisateur connecté sur le bureau
                if (info.State == WTSActive && info.SessionId != 0)
                    yield return info.SessionId;
            }
        }
        finally
        {
            WTSFreeMemory(pInfo);
        }
    }

    // ── Vérification de présence ─────────────────────────────────────────────

    private static bool IsTrayRunningInSession(string processName, uint sessionId)
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try
            {
                if ((uint)p.SessionId == sessionId)
                    return true;
            }
            catch { /* processus déjà terminé */ }
            finally { p.Dispose(); }
        }
        return false;
    }

    // ── Lancement dans une session utilisateur ───────────────────────────────

    private static bool LaunchInSession(string exePath, uint sessionId, ILogger logger)
    {
        if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
        {
            logger.LogWarning(
                "WTSQueryUserToken échoué pour la session {Id} (erreur Win32 {Err}). " +
                "Le service doit tourner en LocalSystem pour lancer des processus dans les sessions utilisateur.",
                sessionId, Marshal.GetLastWin32Error());
            return false;
        }

        try
        {
            if (!DuplicateTokenEx(
                    userToken,
                    TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY,
                    IntPtr.Zero,
                    ImpersonationLevel: 2,  // SecurityImpersonation
                    TokenType: 1,           // TokenPrimary
                    out IntPtr primaryToken))
            {
                logger.LogWarning("DuplicateTokenEx échoué pour la session {Id} (erreur Win32 {Err}).",
                    sessionId, Marshal.GetLastWin32Error());
                return false;
            }

            if (!CreateEnvironmentBlock(out IntPtr envBlock, primaryToken, false))
            {
                logger.LogWarning("CreateEnvironmentBlock échoué pour la session {Id} (erreur Win32 {Err}).",
                    sessionId, Marshal.GetLastWin32Error());
                envBlock = IntPtr.Zero;
            }

            try
            {
                var si = new STARTUPINFO
                {
                    cb          = Marshal.SizeOf<STARTUPINFO>(),
                    lpDesktop   = "winsta0\\default",
                    dwFlags     = STARTF_USESHOWWINDOW,
                    wShowWindow = SW_SHOWNORMAL
                };

                uint creationFlags = NORMAL_PRIORITY_CLASS
                    | (envBlock != IntPtr.Zero ? CREATE_UNICODE_ENVIRONMENT : 0);

                bool ok = CreateProcessAsUser(
                    primaryToken,
                    exePath, null,
                    IntPtr.Zero, IntPtr.Zero,
                    bInheritHandles: false,
                    creationFlags,
                    envBlock,
                    Path.GetDirectoryName(exePath),
                    ref si, out var pi);

                if (!ok)
                {
                    logger.LogWarning("CreateProcessAsUser échoué pour la session {Id} (erreur Win32 {Err}).",
                        sessionId, Marshal.GetLastWin32Error());
                    return false;
                }

                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return true;
            }
            finally
            {
                if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
                CloseHandle(primaryToken);
            }
        }
        finally
        {
            CloseHandle(userToken);
        }
    }
}
