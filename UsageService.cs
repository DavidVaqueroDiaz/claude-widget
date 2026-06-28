using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClaudeWidget;

/// <summary>Foto del consumo en un momento dado, leída de ccusage.</summary>
public class UsageSnapshot
{
    public bool Ok { get; set; }
    public string? Error { get; set; }

    public long TodayTokens { get; set; }
    public double TodayCost { get; set; }

    public double MonthCost { get; set; }
    public long MonthTokens { get; set; }

    public long WeekTokens { get; set; }
    public double WeekCost { get; set; }

    // Ventana móvil de 5 horas (lo que de verdad limita Claude)
    public bool HasBlock { get; set; }
    public long BlockTokens { get; set; }
    public double BlockCost { get; set; }
    public int BlockRemainingMinutes { get; set; }
    public double BurnTokensPerMin { get; set; }
    public double ProjectedBlockCost { get; set; }
    public long ProjectedBlockTokens { get; set; }

    public string? Model { get; set; }
}

/// <summary>
/// Llama a ccusage (instalado global) y traduce su JSON a un UsageSnapshot.
/// No modifica nada: solo lee tu uso.
/// </summary>
public static class UsageService
{
    private static string ResolveCcusage()
    {
        // ccusage global vive normalmente en %APPDATA%\npm\ccusage.cmd
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var cmd = Path.Combine(appData, "npm", "ccusage.cmd");
        return File.Exists(cmd) ? cmd : "ccusage"; // si no, confiamos en el PATH
    }

    private static async Task<string?> RunAsync(string args)
    {
        try
        {
            var ccusage = ResolveCcusage();
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{ccusage}\" {args}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            // Drenar AMBOS flujos a la vez para evitar el deadlock de tuberías,
            // y con timeout para no quedar colgados si ccusage no termina.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
            string stdout = await outTask;
            await errTask;
            return string.IsNullOrWhiteSpace(stdout) ? null : stdout;
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? LastOf(string? json, string arrayProp)
    {
        if (json == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(arrayProp, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                JsonElement? last = null;
                foreach (var e in arr.EnumerateArray()) last = e.Clone();
                return last;
            }
        }
        catch { }
        return null;
    }

    private static double GetD(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static long GetL(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    public static async Task<UsageSnapshot> GetAsync()
    {
        var snap = new UsageSnapshot();
        try
        {
            var dailyJson = await RunAsync("daily --json");
            var monthlyJson = await RunAsync("monthly --json");
            var weeklyJson = await RunAsync("weekly --json");
            var blockJson = await RunAsync("blocks --active --json");

            var today = LastOf(dailyJson, "daily");
            if (today is { } t)
            {
                snap.TodayTokens = GetL(t, "totalTokens");
                snap.TodayCost = GetD(t, "totalCost");
                if (t.TryGetProperty("modelsUsed", out var mu) && mu.ValueKind == JsonValueKind.Array)
                {
                    string? best = null;
                    foreach (var m in mu.EnumerateArray())
                    {
                        var name = m.GetString();
                        if (name != null && name.Contains("opus")) best = name;
                        else best ??= name;
                    }
                    snap.Model = Prettify(best);
                }
            }

            var month = LastOf(monthlyJson, "monthly");
            if (month is { } mo)
            {
                snap.MonthCost = GetD(mo, "totalCost");
                snap.MonthTokens = GetL(mo, "totalTokens");
            }

            var week = LastOf(weeklyJson, "weekly");
            if (week is { } wk)
            {
                snap.WeekCost = GetD(wk, "totalCost");
                snap.WeekTokens = GetL(wk, "totalTokens");
            }

            var block = LastOf(blockJson, "blocks");
            if (block is { } b)
            {
                snap.HasBlock = true;
                snap.BlockTokens = GetL(b, "totalTokens");
                snap.BlockCost = GetD(b, "costUSD");
                if (b.TryGetProperty("burnRate", out var br) && br.ValueKind == JsonValueKind.Object)
                    snap.BurnTokensPerMin = GetD(br, "tokensPerMinute");
                if (b.TryGetProperty("projection", out var pr) && pr.ValueKind == JsonValueKind.Object)
                {
                    snap.BlockRemainingMinutes = (int)GetL(pr, "remainingMinutes");
                    snap.ProjectedBlockCost = GetD(pr, "totalCost");
                    snap.ProjectedBlockTokens = GetL(pr, "totalTokens");
                }
            }

            snap.Ok = today != null || block != null;
            if (!snap.Ok) snap.Error = "ccusage no devolvió datos";
        }
        catch (Exception ex)
        {
            snap.Ok = false;
            snap.Error = ex.Message;
        }
        return snap;
    }

    private static string? Prettify(string? model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        if (model.Contains("opus")) return "Opus";
        if (model.Contains("sonnet")) return "Sonnet";
        if (model.Contains("haiku")) return "Haiku";
        return model;
    }
}
