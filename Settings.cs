using System.IO;
using System.Text.Json;

namespace ClaudeWidget;

/// <summary>
/// Ajustes del widget. Se guardan en %APPDATA%\ClaudeWidget\settings.json
/// para recordar posición, qué datos mostrar y las preferencias de aviso.
/// </summary>
public class Settings
{
    // --- Posición y ventana ---
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 80;
    public bool AlwaysOnTop { get; set; } = true;

    // --- Qué datos de consumo mostrar (Ajustes, Parte 2) ---
    public bool ShowTodayTokens { get; set; } = true;
    public bool ShowTodayCost { get; set; } = true;
    public bool ShowMonthCost { get; set; } = true;
    public bool ShowBlockWindow { get; set; } = true;   // ventana de 5h
    public bool ShowBurnRate { get; set; } = true;       // ritmo de gasto
    public bool ShowProjection { get; set; } = true;     // proyección de la ventana
    public bool ShowModel { get; set; } = true;          // modelo en uso

    // --- Barras de consumo (verde → ámbar → rojo) ---
    public bool ShowWeekBar { get; set; } = true;        // barra del límite semanal
    public bool ShowBlockBar { get; set; } = true;       // barra de la sesión (5h)
    public bool Calibrated { get; set; } = false;        // (obsoleto)

    // Datos EXACTOS del plan vía token de `claude login` (método Clawdmeter).
    public bool UsePlanApi { get; set; } = true;

    // Ruta opcional a cli.mjs del approver (si lo tienes clonado en local).
    // Vacío = se usa el comando global "claude-remote-approver" (npm i -g).
    public string ApproverCliPath { get; set; } = "";

    // Calibración a partir de una captura de /usage:
    //  - guardamos el % visto y los tokens de ccusage en ese momento (para estimar)
    //  - guardamos cuándo se reinicia (sesión = instante; semanal = día+hora fijos)
    public double CalSessionPct { get; set; }
    public long CalSessionTokens { get; set; }
    public long SessionResetUnixMs { get; set; }         // instante de reinicio de la sesión 5h

    public double CalWeekPct { get; set; }
    public long CalWeekTokens { get; set; }
    public int WeekResetDow { get; set; } = 4;           // 0=Dom..6=Sáb (4=Jueves)
    public int WeekResetHour { get; set; } = 11;
    public int WeekResetMinute { get; set; }
    // Umbrales de color del bicho (fracción 0..1)
    public double AmberThreshold { get; set; } = 0.70;
    public double RedThreshold { get; set; } = 0.90;

    // El bicho reacciona a los tokens reales de la ventana de 5h: este es el
    // tope a partir del cual se considera "al límite" (solo afecta a la cara del bicho).
    public long SessionMascotBudget { get; set; } = 150_000_000;

    // --- Avisos (Parte 3) ---
    public bool BlinkOnWaiting { get; set; } = true;
    public bool SoundOnWaiting { get; set; } = true;
    // Una petición pendiente más vieja que esto se considera obsoleta (hook caído)
    // y se ignora para no avisar eternamente. En minutos.
    public int StalePendingMinutes { get; set; } = 360;

    // --- Intervalos (segundos) ---
    public int UsageRefreshSeconds { get; set; } = 60;   // ccusage es lento: no abusar
    public int PendingPollSeconds { get; set; } = 2;     // leer el archivo pending es barato

    // --- Plan (referencia de límites) ---
    public string Plan { get; set; } = "Max 5x";

    // --- Arranque y visibilidad ---
    public bool StartWithWindows { get; set; } = true;   // arrancar al iniciar Windows
    public bool LinkToClaude { get; set; } = true;       // mostrar solo cuando Claude está abierto

    // ------------------------------------------------------------------
    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeWidget");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<Settings>(json);
                if (s != null) return s;
            }
        }
        catch { /* si está corrupto, empezamos con valores por defecto */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* no bloquear el widget si falla el guardado */ }
    }
}
