using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClaudeWidget;

/// <summary>Uso EXACTO del plan (igual que /usage), si hay token de claude login.</summary>
public class PlanUsage
{
    public bool Ok { get; set; }
    public string? Error { get; set; }   // "no-token" | "auth" | "http-xxx" | otro
    public double SessionPct { get; set; }
    public double WeekPct { get; set; }
    public long SessionResetUnix { get; set; }   // segundos Unix
    public long WeekResetUnix { get; set; }
}

/// <summary>
/// Lee el dato EXACTO del plan, igual que el panel /usage, con el MISMO método
/// que el proyecto público Clawdmeter: lee tu token del archivo en claro
/// .credentials.json (el que crea `claude login`, SIN descifrar nada) y consulta
/// el endpoint NO oficial /api/oauth/usage. No toca el almacén cifrado de la app.
/// </summary>
public static class UsageApiService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
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

    private static string? ReadAccessToken()
    {
        var p = FindCredentialsFile();
        if (p == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            return FindString(doc.RootElement, "accessToken");
        }
        catch { return null; }
    }

    // Busca recursivamente una propiedad string por nombre (token anidado o directo).
    private static string? FindString(JsonElement el, string name)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.NameEquals(name) && prop.Value.ValueKind == JsonValueKind.String)
                        return prop.Value.GetString();
                    var r = FindString(prop.Value, name);
                    if (r != null) return r;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    var r = FindString(item, name);
                    if (r != null) return r;
                }
                break;
        }
        return null;
    }

    public static bool HasToken() => ReadAccessToken() != null;

    public static async Task<PlanUsage> GetAsync()
    {
        var pu = new PlanUsage();
        var token = ReadAccessToken();
        if (string.IsNullOrEmpty(token)) { pu.Error = "no-token"; return pu; }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);   // necesario o da 429
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.Unauthorized) { pu.Error = "auth"; return pu; }
            if (!resp.IsSuccessStatusCode) { pu.Error = "http-" + (int)resp.StatusCode; return pu; }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("five_hour", out var fh))
            {
                pu.SessionPct = GetUtil(fh);
                pu.SessionResetUnix = GetReset(fh);
            }
            if (root.TryGetProperty("seven_day", out var sd))
            {
                pu.WeekPct = GetUtil(sd);
                pu.WeekResetUnix = GetReset(sd);
            }
            pu.Ok = true;
        }
        catch (Exception ex) { pu.Error = ex.Message; }
        return pu;
    }

    private static double GetUtil(JsonElement obj)
        => obj.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;

    private static long GetReset(JsonElement obj)
    {
        if (!obj.TryGetProperty("resets_at", out var r)) return 0;
        if (r.ValueKind == JsonValueKind.Number)
        {
            double v = r.GetDouble();
            return v > 1e12 ? (long)(v / 1000) : (long)v; // ms o s
        }
        if (r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var dto))
            return dto.ToUnixTimeSeconds();
        return 0;
    }
}
