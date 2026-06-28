using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClaudeWidget;

public partial class MainWindow : Window
{
    private Settings _settings;
    private readonly DispatcherTimer _usageTimer;
    private readonly DispatcherTimer _pendingTimer;
    private readonly DispatcherTimer _claudeTimer = new();
    private bool _claudeWasRunning;
    private bool _loadingUsage;
    private UsageSnapshot? _lastSnap;
    private PlanUsage? _plan;
    private int _page = 1;

    // --- Estado para los gestos del bicho ---
    private bool _pendingActive;                 // hay permiso esperando
    private long _lastActivityTick = Environment.TickCount64;
    private long _lastActivityUnixMs;            // última actividad real de Claude (ms Unix)
    private long _prevBlockTokens = -1;
    private double _prevSessionPct = -1;
    private long _celebrateUntil;                // bailar tras reinicio de límite
    private long _demoUntil;                     // override por click derecho
    private string _demoGesture = "";
    private int _idleVariantIdx;
    private const long IdleSleepMs = 8 * 60 * 1000;   // 8 min sin actividad -> dormir
    private static readonly string[] _idleVariants =
        { "idle_breathe", "idle_blink", "idle_look_around", "idle_breathe", "expression_wink" };

    // Estado del aviso de permiso
    private string? _lastPendingId;
    private string? _respondedId;             // id que el usuario ya respondió/descartó
    private bool _alerting;
    private bool _pollBusy;
    private long _suppressPendingUntil;       // ignora pending durante unos segundos tras actuar
    private Storyboard? _blink;

    public MainWindow()
    {
        InitializeComponent();
        _settings = Settings.Load();
        ApproverService.CliPath = string.IsNullOrWhiteSpace(_settings.ApproverCliPath) ? null : _settings.ApproverCliPath;

        Left = _settings.Left;
        Top = _settings.Top;
        Topmost = _settings.AlwaysOnTop;

        _usageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(15, _settings.UsageRefreshSeconds)) };
        _usageTimer.Tick += async (_, _) => await RefreshUsageAsync();

        _pendingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, _settings.PendingPollSeconds)) };
        _pendingTimer.Tick += async (_, _) => await PollApproverAsync();

        _claudeTimer.Interval = TimeSpan.FromSeconds(4);
        _claudeTimer.Tick += (_, _) => CheckClaude();

        Loaded += async (_, _) =>
        {
            EnsureOnScreen();
            SetPage(1);

            // Parte 4: arranque automático + visibilidad ligada a Claude.
            Autostart.Apply(_settings.StartWithWindows);
            _claudeWasRunning = IsClaudeDesktopRunning();
            if (_settings.LinkToClaude && !_claudeWasRunning) Hide();
            _claudeTimer.Start();

            await PollApproverAsync();
            _pendingTimer.Start();
            await RefreshUsageAsync();
            _usageTimer.Start();
        };
    }

    // ---------------- Parte 4: mostrar/ocultar con la app de Claude ----------------
    private void CheckClaude()
    {
        if (!_settings.LinkToClaude) return;
        bool running = IsClaudeDesktopRunning();
        if (running && !_claudeWasRunning) ShowWidget();        // Claude se abrió → aparecer
        else if (!running && _claudeWasRunning) Hide();          // Claude se cerró → ocultar
        _claudeWasRunning = running;
    }

    private void ShowWidget()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Topmost = _settings.AlwaysOnTop;
    }

    private static bool IsClaudeDesktopRunning()
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName("claude"); }
        catch { return false; }
        try
        {
            foreach (var p in procs)
                try { if (p.MainWindowHandle != IntPtr.Zero) return true; } catch { }
            return false;
        }
        finally
        {
            foreach (var p in procs) try { p.Dispose(); } catch { }
        }
    }

    // ---------------- Arrastrar la ventana ----------------
    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Save();
        }
    }

    private void EnsureOnScreen()
    {
        var w = SystemParameters.VirtualScreenWidth;
        var h = SystemParameters.VirtualScreenHeight;
        if (Left < 0 || Top < 0 || Left > w - 50 || Top > h - 50) { Left = 80; Top = 80; }
    }

    // ---------------- Botones de cabecera ----------------
    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshUsageAsync();

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        dlg.ShowDialog();
        // Reaplicar ajustes en caliente.
        Topmost = _settings.AlwaysOnTop;
        _usageTimer.Interval = TimeSpan.FromSeconds(Math.Max(15, _settings.UsageRefreshSeconds));
        if (_lastSnap != null) RenderUsage(_lastSnap);
    }

    private void HideBtn_Click(object sender, RoutedEventArgs e) => Hide();

    // ---------------- Navegación entre pantallas ----------------
    private void PrevPage_Click(object sender, RoutedEventArgs e) => SetPage(_page == 1 ? 2 : 1);
    private void NextPage_Click(object sender, RoutedEventArgs e) => SetPage(_page == 1 ? 2 : 1);

    private void SetPage(int page)
    {
        _page = page;
        Page1.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        Page2.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- Aviso de permiso (lee tu approver, no lo modifica) ----------------
    private async Task PollApproverAsync()
    {
        if (_pollBusy) return;
        _pollBusy = true;
        try
        {
            bool free = ApproverService.IsFreeMode();
            SetFreeModeButton(free);

            PendingInfo? pending = free ? null : ApproverService.ReadPending();

            // Descartar peticiones obsoletas (hook caído): Since está en milisegundos.
            if (pending != null && IsStale(pending)) pending = null;

            // Ya resuelta: si Claude siguió trabajando (creció el uso) DESPUÉS de
            // crearse esta petición, es que ya se contestó (aunque el approver no
            // borrara el archivo, p.ej. preguntas respondidas dentro del chat).
            if (pending != null && pending.Since > 0 && _lastActivityUnixMs > pending.Since + 3000)
                pending = null;

            // Tras pulsar Aprobar/Denegar ignoramos el archivo unos segundos.
            if (Environment.TickCount64 < _suppressPendingUntil) pending = null;

            // No re-avisar de algo que el usuario acaba de responder o descartar.
            if (pending != null && pending.RequestId == _respondedId) { HidePending(); return; }

            if (pending != null)
            {
                bool isNew = _lastPendingId != pending.RequestId;
                ShowPending(pending);
                if (isNew) { _lastPendingId = pending.RequestId; OnNewPending(); }
                else EnsureVisibleForPending();   // si la ocultaste, vuelve a aparecer

                // ¿Ya la respondiste en el móvil / la app / el propio widget? -> quitar aviso.
                if (await ApproverService.WasAnsweredAsync(pending.RequestId, pending.Since))
                {
                    _respondedId = pending.RequestId;
                    HidePending();
                }
            }
            else
            {
                _lastPendingId = null;
                _respondedId = null;
                HidePending();
            }

            UpdateCritter();   // refleja permiso/uso/inactividad en el bicho
        }
        finally { _pollBusy = false; }
    }

    private bool IsStale(PendingInfo p)
    {
        if (p.Since <= 0) return false;
        try
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds(p.Since);
            return (DateTimeOffset.UtcNow - when).TotalMinutes > _settings.StalePendingMinutes;
        }
        catch { return false; }
    }

    private void EnsureVisibleForPending()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
    }

    private void DismissPending_Click(object sender, RoutedEventArgs e)
    {
        _respondedId = _lastPendingId;            // ocultar localmente sin denegar
        _suppressPendingUntil = Environment.TickCount64 + 4000;
        HidePending();
    }

    private void ShowPending(PendingInfo p)
    {
        _pendingActive = true;
        PendingPanel.Visibility = Visibility.Visible;
        PendingTitle.Text = p.Title;
        PendingMsg.Text = p.Message;
        StatusDot.Fill = Hex("#F87171");
        if (_settings.BlinkOnWaiting) StartBlink();
    }

    private void HidePending()
    {
        _pendingActive = false;
        PendingPanel.Visibility = Visibility.Collapsed;
        StatusDot.Fill = Hex("#4ADE80");
        StopBlink();
    }

    private void OnNewPending()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        SetPage(1);
        Activate();
        if (_settings.SoundOnWaiting)
        {
            try { SystemSounds.Exclamation.Play(); } catch { }
        }
    }

    private void StartBlink()
    {
        if (_alerting) return;
        _alerting = true;
        _blink = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var anim = new DoubleAnimation(0.0, 0.9, TimeSpan.FromSeconds(0.55))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(anim, AlertOverlay);
        Storyboard.SetTargetProperty(anim, new PropertyPath(OpacityProperty));
        _blink.Children.Add(anim);
        _blink.Begin();
    }

    private void StopBlink()
    {
        if (!_alerting) return;
        _alerting = false;
        _blink?.Stop();
        _blink = null;
        AlertOverlay.Opacity = 0;
    }

    private async void Approve_Click(object sender, RoutedEventArgs e) => await RespondAsync(approve: true);
    private async void Deny_Click(object sender, RoutedEventArgs e) => await RespondAsync(approve: false);

    private async Task RespondAsync(bool approve)
    {
        ApproveBtn.IsEnabled = DenyBtn.IsEnabled = false;
        _suppressPendingUntil = Environment.TickCount64 + 6000;
        _respondedId = _lastPendingId;
        HidePending();

        ApproverResult res;
        try
        {
            res = approve ? await ApproverService.ApproveAsync() : await ApproverService.DenyAsync();
        }
        finally
        {
            ApproveBtn.IsEnabled = DenyBtn.IsEnabled = true;
        }

        if (!res.Ok)
        {
            _respondedId = null; // no se envió: permitir reintento y re-aviso
            MessageBox.Show("No se pudo enviar la respuesta al approver.\n\n" + (res.Detail ?? "") +
                            "\n\nPuedes responder desde el móvil o el terminal.", "Claude Widget");
        }
        await PollApproverAsync();
    }

    // ---------------- "Haz lo que quieras" (barra libre) ----------------
    private async void FreeMode_Click(object sender, RoutedEventArgs e)
    {
        bool free = ApproverService.IsFreeMode();
        FreeToggle.IsEnabled = false;
        ApproverResult res;
        try
        {
            res = free ? await ApproverService.FreeModeOffAsync() : await ApproverService.FreeModeOnAsync();
        }
        finally
        {
            FreeToggle.IsEnabled = true;
        }
        if (!res.Ok)
            MessageBox.Show("No se pudo cambiar la aceptación automática.\n\n" + (res.Detail ?? ""), "Claude Widget");
        await PollApproverAsync();
    }

    private void SetFreeModeButton(bool on)
    {
        FreeToggle.IsChecked = on;
    }

    // ---------------- Consumo ----------------
    private async Task RefreshUsageAsync()
    {
        if (_loadingUsage) return;
        _loadingUsage = true;
        try
        {
            var snap = await UsageService.GetAsync();
            _plan = _settings.UsePlanApi ? await UsageApiService.GetAsync() : null;

            // Actividad: ¿han crecido los tokens de la ventana de 5h?
            if (snap.Ok)
            {
                if (_prevBlockTokens >= 0 && snap.BlockTokens > _prevBlockTokens)
                {
                    _lastActivityTick = Environment.TickCount64;
                    _lastActivityUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                _prevBlockTokens = snap.BlockTokens;
            }
            // Celebración: ¿se ha reiniciado la sesión? (% cae de alto a casi 0)
            if (_plan is { Ok: true } p2)
            {
                if (_prevSessionPct >= 15 && p2.SessionPct <= 3)
                    _celebrateUntil = Environment.TickCount64 + 7000;
                _prevSessionPct = p2.SessionPct;
            }
            _idleVariantIdx++;   // cambia el gesto de reposo cada refresco (variedad)

            RenderUsage(snap);
        }
        finally { _loadingUsage = false; }
    }

    private void RenderUsage(UsageSnapshot s)
    {
        _lastSnap = s;
        MetricsPanel.Children.Clear();
        SummaryPanel.Children.Clear();

        // ---------- Pantalla «Uso» ----------
        if (_plan is { Ok: true } pl)
        {
            // Dato EXACTO del plan (igual que /usage): barras reales.
            double sf = pl.SessionPct / 100.0;
            double wf = pl.WeekPct / 100.0;
            AddBar("Sesión (5h)", sf, ResetCountdown(pl.SessionResetUnix));
            AddBar("Semanal", wf, ResetClock(pl.WeekResetUnix));
        }
        else
        {
            // Sin token (o caducado): mostramos lo exacto de ccusage; el % del plan
            // se activa con `claude login` (ver pie).
            if (s.Ok)
            {
                AddSummary("Hoy", $"{FormatTokens(s.TodayTokens)}  ·  {FormatCost(s.TodayCost)}", null);
                AddSummary("Semana", $"{FormatTokens(s.WeekTokens)}  ·  {FormatCost(s.WeekCost)}", null);
                if (s.HasBlock)
                    AddSummary("Sesión 5h", $"{FormatTokens(s.BlockTokens)}  ·  {FormatCost(s.BlockCost)}",
                               s.BlockRemainingMinutes > 0 ? $"quedan {FormatMinutes(s.BlockRemainingMinutes)}" : null);
            }
        }

        // ---------- Pantalla «Detalles» (siempre ccusage exacto) ----------
        if (s.Ok)
        {
            if (_settings.ShowModel && !string.IsNullOrEmpty(s.Model)) AddMetric("Modelo", s.Model!, "#A5B4FC");
            if (_settings.ShowTodayTokens) AddMetric("Hoy (tokens)", FormatTokens(s.TodayTokens), "#FFFFFF");
            if (_settings.ShowTodayCost) AddMetric("Hoy ($ API)", FormatCost(s.TodayCost), "#FFFFFF");
            AddMetric("Semana (tokens)", FormatTokens(s.WeekTokens), "#FFFFFF");
            if (_settings.ShowMonthCost) AddMetric("Mes ($ API)", FormatCost(s.MonthCost), "#FFFFFF");
            if (s.HasBlock && _settings.ShowBurnRate) AddMetric("Ritmo", $"{FormatTokens((long)s.BurnTokensPerMin)}/min", "#FCD34D");
            if (s.HasBlock && _settings.ShowProjection && s.BlockRemainingMinutes > 0) AddMetric("Quedan en 5h", FormatMinutes(s.BlockRemainingMinutes), "#FCD34D");
        }
        else if (_plan is not { Ok: true })
        {
            AddMetric("Consumo", "no disponible", "#F87171");
        }

        FooterText.Text = PlanFooter() + $" · {DateTime.Now:HH:mm}";
        UpdateCritter();
    }

    private void AddMetric(string label, string value, string valueHex)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lbl, 0);
        var val = new TextBlock { Text = value, Foreground = Hex(valueHex), FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(val, 1);
        grid.Children.Add(lbl);
        grid.Children.Add(val);
        MetricsPanel.Children.Add(grid);
    }

    // ---------------- Fila de resumen exacto (etiqueta · valor + subtexto) ----------------
    private void AddSummary(string label, string value, string? sub)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lbl = new TextBlock { Text = label, Foreground = Hex("#B0FFFFFF"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var val = new TextBlock { Text = value, Foreground = Hex("#FFFFFF"), FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(val, 1);
        top.Children.Add(lbl);
        top.Children.Add(val);
        sp.Children.Add(top);

        if (!string.IsNullOrEmpty(sub))
            sp.Children.Add(new TextBlock { Text = sub, Foreground = Hex("#70FFFFFF"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right });

        SummaryPanel.Children.Add(sp);
    }

    // ---------------- Barra de progreso (dato real del plan) ----------------
    private void AddBar(string title, double frac, string subText)
    {
        frac = Math.Max(0, Math.Min(1, frac));
        int pct = (int)Math.Round(frac * 100);
        var color = LevelColor(frac);

        var container = new StackPanel { Margin = new Thickness(0, 7, 0, 5) };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title2 = new TextBlock { Text = title, Foreground = Hex("#DDFFFFFF"), FontSize = 12, FontWeight = FontWeights.SemiBold };
        var pctText = new TextBlock { Text = $"{pct}% usado", Foreground = new SolidColorBrush(color), FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(title2, 0);
        Grid.SetColumn(pctText, 1);
        top.Children.Add(title2);
        top.Children.Add(pctText);

        var track = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = Hex("#FF2C2C34"), Margin = new Thickness(0, 5, 0, 0) };
        var barGrid = new Grid();
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - frac, GridUnitType.Star) });
        var fill = new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(color) };
        Grid.SetColumn(fill, 0);
        barGrid.Children.Add(fill);
        track.Child = barGrid;

        container.Children.Add(top);
        container.Children.Add(track);
        if (!string.IsNullOrEmpty(subText))
            container.Children.Add(new TextBlock { Text = subText, Foreground = Hex("#80FFFFFF"), FontSize = 10, Margin = new Thickness(0, 4, 0, 0) });
        SummaryPanel.Children.Add(container);
    }

    private Color LevelColor(double frac)
    {
        if (frac >= _settings.RedThreshold) return Color.FromRgb(0xF8, 0x71, 0x71);
        if (frac >= _settings.AmberThreshold) return Color.FromRgb(0xFC, 0xD3, 0x4D);
        return Color.FromRgb(0x4A, 0xDE, 0x80);
    }

    private string ResetCountdown(long unixSec)
    {
        if (unixSec <= 0) return "";
        var left = DateTimeOffset.FromUnixTimeSeconds(unixSec) - DateTimeOffset.Now;
        return $"se reinicia en {FormatMinutes((int)Math.Max(0, left.TotalMinutes))}";
    }

    private string ResetClock(long unixSec)
    {
        if (unixSec <= 0) return "";
        // +30s para redondear al minuto (Claude muestra 11:00, no 10:59:59).
        var dt = DateTimeOffset.FromUnixTimeSeconds(unixSec).ToLocalTime().AddSeconds(30);
        return $"se reinicia {DayAbbr[(int)dt.DayOfWeek]} {dt:HH:mm}";
    }

    private string PlanFooter()
    {
        if (_plan is { Ok: true }) return "% del plan: exacto ✓";
        if (!_settings.UsePlanApi) return "% del plan desactivado";
        return _plan?.Error switch
        {
            "no-token" => "% del plan: ejecuta «claude login»",
            "auth" => "% del plan: token caducado → «claude login»",
            _ => "% del plan no disponible (ccusage exacto)"
        };
    }

    // ---------------- Reinicio semanal (día y hora fijos -> contador exacto) ----------------
    private DateTimeOffset WeekResetNext()
    {
        var now = DateTimeOffset.Now;
        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day,
            _settings.WeekResetHour, _settings.WeekResetMinute, 0, now.Offset);
        int diff = ((_settings.WeekResetDow - (int)now.DayOfWeek) + 7) % 7;
        candidate = candidate.AddDays(diff);
        if (candidate <= now) candidate = candidate.AddDays(7);
        return candidate;
    }

    private static readonly string[] DayAbbr = { "dom", "lun", "mar", "mié", "jue", "vie", "sáb" };

    private string WeekResetText()
    {
        var r = WeekResetNext();
        return $"{DayAbbr[(int)r.DayOfWeek]} {r:HH:mm}";
    }

    private string MoodFor(double frac)
    {
        if (frac >= _settings.RedThreshold) return "¡Estoy malito! 🤢";
        if (frac >= _settings.AmberThreshold) return "Uff, qué nervios… 😰";
        if (frac >= 0.50) return "Voy tirando 🙂";
        return "¡Como nuevo! 😄";
    }

    // ---------------- Selector de gesto del bicho (por prioridad) ----------------
    private void UpdateCritter()
    {
        long now = Environment.TickCount64;

        // El click derecho (demo) manda durante unos segundos.
        if (now < _demoUntil && _demoGesture.Length > 0)
        {
            Critter.Play(_demoGesture);
            return;
        }

        double frac = _plan is { Ok: true } pl
            ? Math.Max(pl.SessionPct, pl.WeekPct) / 100.0
            : (_settings.SessionMascotBudget > 0 && _lastSnap != null
                ? (double)_lastSnap.BlockTokens / _settings.SessionMascotBudget : 0);

        string g, mood;
        if (_pendingActive) { g = "expression_surprise"; mood = "¡Atiende, te espero! 👀"; }
        else if (now < _celebrateUntil) { g = "dance_bounce_dj"; mood = "¡Límite reiniciado! 🎉"; }
        else if (frac >= _settings.RedThreshold) { g = "expression_surprise"; mood = "¡Casi al límite! 😱"; }
        else if (frac >= _settings.AmberThreshold) { g = "work_think"; mood = "Concentrado… 🤔"; }
        else if (now - _lastActivityTick > IdleSleepMs) { g = "expression_sleep"; mood = "Zzz… 😴"; }
        else { g = _idleVariants[_idleVariantIdx % _idleVariants.Length]; mood = "Todo bien 🙂"; }

        Critter.Play(g);
        MoodText.Text = mood;
    }

    // ---------------- Showcase: click derecho reproduce TODOS los gestos seguidos ----------------
    private static readonly string[] _demoGestures =
    {
        "idle_breathe", "idle_blink", "idle_look_around",
        "expression_wink", "expression_surprise", "expression_sleep",
        "work_coding", "work_think",
        "dance_bounce", "dance_sway", "dance_bounce_dj", "dance_sway_dj", "dance_djmix"
    };
    private DispatcherTimer? _showcaseTimer;
    private int _showcaseIdx;

    private void Critter_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        StartShowcase();
        e.Handled = true;
    }

    public void StartShowcase()
    {
        _showcaseIdx = 0;
        _showcaseTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _showcaseTimer.Tick -= Showcase_Tick;
        _showcaseTimer.Tick += Showcase_Tick;
        ShowcaseStep();
        _showcaseTimer.Start();
    }

    private void Showcase_Tick(object? sender, EventArgs e)
    {
        _showcaseIdx++;
        if (_showcaseIdx >= _demoGestures.Length)
        {
            _showcaseTimer?.Stop();
            _demoUntil = 0;          // vuelve al modo automático
            UpdateCritter();
            return;
        }
        ShowcaseStep();
    }

    private void ShowcaseStep()
    {
        var name = _demoGestures[_showcaseIdx];
        _demoGesture = name;
        _demoUntil = Environment.TickCount64 + 60000;   // mantener el gesto durante el showcase
        Critter.Play(name);
        MoodText.Text = $"{name.Replace('_', ' ')}  ({_showcaseIdx + 1}/{_demoGestures.Length})";
    }

    // ---------------- Utilidades ----------------
    private static SolidColorBrush Hex(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private static string FormatTokens(long n)
    {
        if (n >= 1_000_000_000) return $"{n / 1_000_000_000.0:0.#}B";
        if (n >= 1_000_000) return $"{n / 1_000_000.0:0.#}M";
        if (n >= 1_000) return $"{n / 1_000.0:0.#}k";
        return n.ToString();
    }

    private static string FormatCost(double c) => $"${c:0.00}";

    private static string FormatMinutes(int m) => m >= 60 ? $"{m / 60}h {m % 60}min" : $"{m}min";

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
        base.OnClosing(e);
    }
}
