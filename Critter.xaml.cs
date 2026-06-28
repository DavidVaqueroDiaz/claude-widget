using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ClaudeWidget;

/// <summary>
/// Bicho de Claude usando los FRAMES OFICIALES (claudepix, de Clawdmeter):
/// rejilla 20×20 indexada a paleta, varios gestos animados.
/// El gesto cambia según el uso; click derecho los muestra todos (demo).
/// Nota: es un asset de marca de Anthropic, para uso personal.
/// </summary>
public partial class Critter : UserControl
{
    private const double Cell = 7;
    private const double OffX = 5;   // (150 - 20*7) / 2
    private const double OffY = 5;

    private sealed class Gesture
    {
        public string[] Palette = System.Array.Empty<string>();
        public List<(int hold, int[][] grid)> Frames = new();
    }

    private readonly Dictionary<string, Gesture?> _cache = new();
    private readonly DispatcherTimer _timer = new();
    private Gesture? _cur;
    private string _curName = "";
    private int _frame;

    public Critter()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => NextFrame();
        Loaded += (_, _) => { if (_cur == null) Play("idle_breathe"); };
    }

    // -------- Estado según uso (0..1): elige el gesto oficial --------
    public void SetStress(double frac, double amber = 0.70, double red = 0.90)
    {
        frac = System.Math.Max(0, System.Math.Min(1, frac));
        string name = frac >= red ? "expression_surprise" : frac >= amber ? "work_think" : "idle_breathe";
        Play(name);
    }

    // -------- Demo: reproducir un gesto concreto --------
    public void Play(string name)
    {
        if (name == _curName && _cur != null) return;
        var g = Get(name);
        if (g == null || g.Frames.Count == 0) return;
        _curName = name;
        _cur = g;
        _frame = 0;
        RenderFrame();
        _timer.Stop();
        _timer.Interval = System.TimeSpan.FromMilliseconds(System.Math.Max(60, _cur.Frames[0].hold));
        _timer.Start();
    }

    private void NextFrame()
    {
        if (_cur == null || _cur.Frames.Count == 0) return;
        _frame = (_frame + 1) % _cur.Frames.Count;
        RenderFrame();
        _timer.Interval = System.TimeSpan.FromMilliseconds(System.Math.Max(60, _cur.Frames[_frame].hold));
    }

    // -------- Dibujo de un frame (una figura sólida por color, sin costuras) --------
    private void RenderFrame()
    {
        Stage.Children.Clear();
        if (_cur == null) return;
        var grid = _cur.Frames[_frame].grid;

        for (int ci = 1; ci < _cur.Palette.Length; ci++)
        {
            string hex = _cur.Palette[ci];
            if (string.Equals(hex, "transparent", System.StringComparison.OrdinalIgnoreCase)) continue;

            var geo = new GeometryGroup { FillRule = FillRule.Nonzero };
            for (int r = 0; r < grid.Length; r++)
            {
                var row = grid[r];
                for (int c = 0; c < row.Length; c++)
                    if (row[c] == ci)
                        geo.Children.Add(new RectangleGeometry(new Rect(OffX + c * Cell, OffY + r * Cell, Cell, Cell)));
            }
            if (geo.Children.Count == 0) continue;

            var path = new Path { Data = geo };
            try { path.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { path.Fill = Brushes.Orange; }
            RenderOptions.SetEdgeMode(path, EdgeMode.Aliased);
            Stage.Children.Add(path);
        }
    }

    // -------- Carga (incrustado en el .exe) --------
    private Gesture? Get(string name)
    {
        if (_cache.TryGetValue(name, out var cached)) return cached;
        Gesture? g = null;
        try
        {
            var asm = typeof(Critter).Assembly;
            using var st = asm.GetManifestResourceStream($"ClaudeWidget.Assets.claudepix.{name}.json");
            if (st != null)
            {
                using var doc = JsonDocument.Parse(st);
                var root = doc.RootElement;
                var pal = root.GetProperty("palette").EnumerateArray().Select(e => e.GetString() ?? "transparent").ToArray();
                var frames = new List<(int, int[][])>();
                foreach (var f in root.GetProperty("frames").EnumerateArray())
                {
                    int hold = f.TryGetProperty("hold", out var h) ? h.GetInt32() : 300;
                    var grid = f.GetProperty("grid").EnumerateArray()
                        .Select(rw => rw.EnumerateArray().Select(c => c.GetInt32()).ToArray()).ToArray();
                    frames.Add((hold, grid));
                }
                g = new Gesture { Palette = pal, Frames = frames };
            }
        }
        catch { g = null; }
        _cache[name] = g;
        return g;
    }
}
