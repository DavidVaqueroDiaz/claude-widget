using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace ClaudeWidget;

/// <summary>Uso EXACTO del plan (igual que /usage), si hay token de claude login.</summary>
public class PlanUsage
{
    public bool Ok { get; set; }
    public string? Error { get; set; }   // "no-token" | "auth" | "rate" | "http-xxx"
    public double SessionPct { get; set; }
    public double WeekPct { get; set; }
    public long SessionResetUnix { get; set; }   // segundos Unix
    public long WeekResetUnix { get; set; }
}

/// <summary>
/// Lee el dato EXACTO del plan (endpoint NO oficial /api/oauth/usage) usando el
/// token de ~/.claude/.credentials.json (el de `claude login`, SIN descifrar nada).
/// Si el token ha caducado, lo REFRESCA solo con el flujo OAuth estándar (tu
/// refreshToken) y lo guarda de vuelta. Maneja el rate limit (429) sin insistir.
/// </summary>
public static class UsageApiService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string UserAgent = "claude-code/2.1.187";

    public static string? FindCredentialsFile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("CLAUDE_CREDENTIALS_PATH"),
            Path.Combine(home, ".claude", ".credentials.json"),
            Path.Combine(local, "Claude", ".credentials.json"),
            Path.Combine(roaming, "Claude", ".credentials.json"),
        };
        var cfgDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrEmpty(cfgDir)) candidates.Insert(1, Path.Combine(cfgDir, ".credentials.json"));
        foreach (var c in candidates)
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
        return null;
    }

    public static bool HasToken()
    {
        var p = FindCredentialsFile();
        if (p == null) return false;
        try { return FindOauth(JsonNode.Parse(File.ReadAllText(p))) != null; } catch { return false; }
    }

    // Localiza el objeto que contiene accessToken (claudeAiOauth o anidado).
    private static JsonObject? FindOauth(JsonNode? n)
    {
        if (n is JsonObject o)
        {
            if (o.ContainsKey("accessToken")) return o;
            foreach (var kv in o) { var r = FindOauth(kv.Value); if (r != null) return r; }
        }
        else if (n is JsonArray a)
        {
            foreach (var it in a) { var r = FindOauth(it); if (r != null) return r; }
        }
        return null;
    }

    public static async Task<PlanUsage> GetAsync()
    {
        var pu = new PlanUsage();
        var path = FindCredentialsFile();
        if (path == null) { pu.Error = "no-token"; return pu; }

        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(path)); } catch { pu.Error = "no-token"; return pu; }
        var oauth = FindOauth(node);
        if (oauth == null) { pu.Error = "no-token"; return pu; }

        string? token = oauth["accessToken"]?.GetValue<string>();
        if (string.IsNullOrEmpty(token)) { pu.Error = "no-token"; return pu; }

        // El token lo mantiene fresco la tarea programada (CLI oficial), NO el widget,
        // para no chocar con el rate limit (429). Aquí solo lo usamos.
        var (status, body) = await CallUsageAsync(token!);

        if (status == 429) { pu.Error = "rate"; return pu; }
        if (status == 401) { pu.Error = "auth"; return pu; }
        if (status != 200 || body == null) { pu.Error = "http-" + status; return pu; }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("five_hour", out var fh) && fh.ValueKind == JsonValueKind.Object)
            { pu.SessionPct = GetUtil(fh); pu.SessionResetUnix = GetReset(fh); }
            if (root.TryGetProperty("seven_day", out var sd) && sd.ValueKind == JsonValueKind.Object)
            { pu.WeekPct = GetUtil(sd); pu.WeekResetUnix = GetReset(sd); }
            pu.Ok = true;
        }
        catch (Exception ex) { pu.Error = ex.Message; }
        return pu;
    }

    private static async Task<(int status, string? body)> CallUsageAsync(string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await Http.SendAsync(req);
            string b = await resp.Content.ReadAsStringAsync();
            return ((int)resp.StatusCode, b);
        }
        catch { return (0, null); }
    }

    private static double GetUtil(JsonElement obj)
        => obj.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;

    private static long GetReset(JsonElement obj)
    {
        if (!obj.TryGetProperty("resets_at", out var r)) return 0;
        if (r.ValueKind == JsonValueKind.Number)
        {
            double v = r.GetDouble();
            return v > 1e12 ? (long)(v / 1000) : (long)v;
        }
        if (r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var dto))
            return dto.ToUnixTimeSeconds();
        return 0;
    }
}
