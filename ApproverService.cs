using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeWidget;

/// <summary>Petición que el approver está esperando ahora mismo.</summary>
public class PendingInfo
{
    public string RequestId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public long Since { get; set; }   // Unix MILISEGUNDOS (Date.now() en el approver)
}

/// <summary>Resultado de una acción del CLI.</summary>
public record ApproverResult(bool Ok, string? Detail);

/// <summary>
/// Puente de SOLO LECTURA + acciones hacia tu "claude-remote-approver".
/// NO modifica su código ni sus hooks: solo lee sus archivos de estado y
/// ejecuta sus propios comandos de CLI (lo mismo que pulsar en el móvil).
/// </summary>
public static class ApproverService
{
    private static readonly string Home =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string PendingPath => Path.Combine(Home, ".claude-remote-approver.pending.json");
    public static string FreeModePath => Path.Combine(Home, ".claude-remote-approver.barra-libre.json");

    // Ruta opcional a cli.mjs (para quien tenga el approver clonado en local).
    // Si está vacía, se usa el comando global "claude-remote-approver" (npm i -g).
    // Se asigna al arrancar desde Settings.ApproverCliPath.
    public static string? CliPath;

    private static string ResolveNode()
    {
        const string std = @"C:\Program Files\nodejs\node.exe";
        return File.Exists(std) ? std : "node";
    }

    private static string? _resolvedCli;
    private static bool _resolvedCliDone;

    /// <summary>
    /// Localiza el cli.mjs del approver: 1) ruta puesta en Ajustes; 2) deducida
    /// del hook PermissionRequest de ~/.claude/settings.json (donde el propio
    /// approver se registró). Si no se encuentra, null = usar comando global.
    /// </summary>
    private static string? ResolveCli()
    {
        if (!string.IsNullOrEmpty(CliPath) && File.Exists(CliPath)) return CliPath;
        if (_resolvedCliDone) return _resolvedCli;
        _resolvedCliDone = true;

        try
        {
            var sp = Path.Combine(Home, ".claude", "settings.json");
            if (File.Exists(sp))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(sp));
                if (doc.RootElement.TryGetProperty("hooks", out var hooks) &&
                    hooks.TryGetProperty("PermissionRequest", out var pr) && pr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in pr.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("hooks", out var hs) || hs.ValueKind != JsonValueKind.Array) continue;
                        foreach (var h in hs.EnumerateArray())
                        {
                            if (!h.TryGetProperty("command", out var c) || c.ValueKind != JsonValueKind.String) continue;
                            var cmd = c.GetString() ?? "";
                            var m = Regex.Match(cmd, "\"([^\"]+\\.mjs)\"");
                            string? path = m.Success ? m.Groups[1].Value : Regex.Match(cmd, "(\\S+\\.mjs)").Groups[1].Value;
                            if (!string.IsNullOrEmpty(path) && File.Exists(path)) { _resolvedCli = path; return path; }
                        }
                    }
                }
            }
        }
        catch { }
        return _resolvedCli;
    }

    /// <summary>Lee la petición pendiente, o null si no hay.</summary>
    public static PendingInfo? ReadPending()
    {
        try
        {
            if (!File.Exists(PendingPath)) return null;
            var json = File.ReadAllText(PendingPath);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<PendingInfo>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    /// <summary>¿Está activada la "barra libre" (aceptación automática)?</summary>
    public static bool IsFreeMode() => ReadFreeMode() is { active: true };

    /// <summary>Estado de la barra libre: activa y desde cuándo (unix segundos).</summary>
    public static (bool active, long sinceSec)? ReadFreeMode()
    {
        try
        {
            if (!File.Exists(FreeModePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(FreeModePath));
            var r = doc.RootElement;
            bool active = r.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
            long since = r.TryGetProperty("since", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
            return (active, since);
        }
        catch { return null; }
    }

    /// <summary>
    /// ¿Pediste DESACTIVAR la barra libre desde el móvil? (botón "Desactivar" del
    /// approver, que publica en el topic de control). Así el widget lo aplica al
    /// momento en vez de esperar a la siguiente petición.
    /// </summary>
    public static async Task<bool> DisableSignalFromPhoneAsync(long sinceSec)
    {
        if (sinceSec <= 0) return false;
        var cfg = LoadNtfyConfig();
        if (cfg == null) return false;
        string url = $"{cfg.Value.server}/{cfg.Value.topic}-control/json?poll=1&since={sinceSec}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (cfg.Value.auth != null) req.Headers.TryAddWithoutValidation("Authorization", "Basic " + cfg.Value.auth);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return false;
            foreach (var line in (await resp.Content.ReadAsStringAsync()).Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var ev = JsonDocument.Parse(line);
                    if (!ev.RootElement.TryGetProperty("message", out var msgEl)) continue;
                    var msg = msgEl.GetString();
                    if (string.IsNullOrEmpty(msg)) continue;
                    using var m = JsonDocument.Parse(msg);
                    if (m.RootElement.TryGetProperty("disableFreeMode", out var d) && d.ValueKind == JsonValueKind.True) return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    // -------- ¿Ya se respondió la petición (en el móvil, widget, etc.)? --------
    // Mira el mismo canal de respuesta de ntfy que usa tu móvil. Si aparece una
    // respuesta para ese requestId, la petición ya está resuelta venga de donde venga.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public static async Task<bool> WasAnsweredAsync(string requestId, long sinceMs)
    {
        if (string.IsNullOrEmpty(requestId)) return false;
        var cfg = LoadNtfyConfig();
        if (cfg == null) return false;

        long sinceSec = Math.Max(0, sinceMs / 1000 - 60);
        string url = $"{cfg.Value.server}/{cfg.Value.topic}-response/json?poll=1&since={sinceSec}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (cfg.Value.auth != null) req.Headers.TryAddWithoutValidation("Authorization", "Basic " + cfg.Value.auth);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return false;
            string text = await resp.Content.ReadAsStringAsync();
            foreach (var line in text.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var ev = JsonDocument.Parse(line);
                    if (!ev.RootElement.TryGetProperty("message", out var msgEl)) continue;
                    string? msg = msgEl.GetString();
                    if (string.IsNullOrEmpty(msg)) continue;
                    using var m = JsonDocument.Parse(msg);
                    if (m.RootElement.TryGetProperty("requestId", out var rid) && rid.GetString() == requestId)
                        return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    private static (string server, string topic, string? auth)? LoadNtfyConfig()
    {
        try
        {
            var p = Path.Combine(Home, ".claude-remote-approver.json");
            if (!File.Exists(p)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            var root = doc.RootElement;
            string topic = root.TryGetProperty("topic", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(topic)) return null;
            string server = (root.TryGetProperty("ntfyServer", out var sv) ? sv.GetString() : null) ?? "https://ntfy.sh";
            server = server.TrimEnd('/');
            string user = root.TryGetProperty("ntfyUsername", out var u) ? u.GetString() ?? "" : "";
            string pass = root.TryGetProperty("ntfyPassword", out var pw) ? pw.GetString() ?? "" : "";
            string? auth = (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
                ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(user + ":" + pass)) : null;
            return (server, topic, auth);
        }
        catch { return null; }
    }

    // -------- Acciones (equivalen a pulsar en el móvil) --------
    // El éxito se comprueba por la SALIDA real del CLI, no solo por el código
    // de salida (el CLI sale con 0 incluso cuando no hace nada).
    public static async Task<ApproverResult> ApproveAsync() => await ActionAsync("approve", "APROBADO");
    public static async Task<ApproverResult> DenyAsync() => await ActionAsync("deny", "DENEGADO");
    public static async Task<ApproverResult> FreeModeOnAsync() => await ActionAsync("barra-on", "ACTIVADA");
    public static async Task<ApproverResult> FreeModeOffAsync() => await ActionAsync("barra-off", "desactivada");

    private static async Task<ApproverResult> ActionAsync(string command, string successMarker)
    {
        var (launched, _, stdout, stderr) = await RunCliAsync(command);
        if (!launched)
            return new ApproverResult(false, "No se pudo ejecutar el approver. Instálalo con: npm i -g claude-remote-approver");

        if (stdout.Contains(successMarker, StringComparison.OrdinalIgnoreCase))
            return new ApproverResult(true, null);

        // No hizo lo esperado: devolvemos el mensaje del CLI (stderr o stdout).
        string detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                        : !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim()
                        : "El approver no confirmó la acción.";
        if (detail.Length > 200) detail = detail[..200];
        return new ApproverResult(false, detail);
    }

    private static async Task<(bool launched, int exit, string stdout, string stderr)> RunCliAsync(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var cli = ResolveCli();
            if (!string.IsNullOrEmpty(cli))
            {
                // Approver local (ruta de Ajustes o deducida del hook): node cli.mjs <comando>
                psi.FileName = ResolveNode();
                psi.ArgumentList.Add(cli);
                psi.ArgumentList.Add(command);
            }
            else
            {
                // Approver instalado global: claude-remote-approver <comando>
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("claude-remote-approver");
                psi.ArgumentList.Add(command);
            }

            using var p = Process.Start(psi);
            if (p == null) return (false, -1, "", "");

            // Leer ambos flujos a la vez (evita deadlock) + timeout.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } }

            string stdout = await outTask;
            string stderr = await errTask;
            return (true, p.HasExited ? p.ExitCode : -1, stdout, stderr);
        }
        catch
        {
            return (false, -1, "", "");
        }
    }
}
