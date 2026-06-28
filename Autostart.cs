using System.Diagnostics;
using Microsoft.Win32;

namespace ClaudeWidget;

/// <summary>
/// Arranque automático al iniciar sesión en Windows, vía la clave Run del usuario
/// (no necesita permisos de administrador). Apunta al .exe que se está ejecutando.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeWidget";

    public static void Apply(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;

            if (enable)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* si falla, no es crítico */ }
    }
}
