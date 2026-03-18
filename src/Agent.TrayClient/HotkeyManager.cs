// HotkeyManager.cs
// Enregistre Ctrl+Win+Alt+Space comme raccourci global via RegisterHotKey.
// Un seul appui déclenche l'événement Pressed, quel que soit l'application active.
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Agent.TrayClient;

sealed class HotkeyManager : NativeWindow, IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    const uint MOD_ALT     = 0x0001;
    const uint MOD_CONTROL = 0x0002;
    const uint MOD_WIN     = 0x0008;
    const int  WM_HOTKEY   = 0x0312;
    const int  HotkeyId    = 0x4F41; // "OA" — évite les conflits avec d'autres apps

    public event EventHandler? Pressed;

    /// <param name="key">
    /// Touche non-modificateur. Par défaut Space (Ctrl+Win+Alt+Espace).
    /// RegisterHotKey exige au moins une touche physique en plus des modificateurs.
    /// </param>
    public HotkeyManager(Keys key = Keys.Space)
    {
        CreateHandle(new CreateParams());
        RegisterHotKey(Handle, HotkeyId, MOD_ALT | MOD_CONTROL | MOD_WIN, (uint)key);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            Pressed?.Invoke(this, EventArgs.Empty);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
    }
}
