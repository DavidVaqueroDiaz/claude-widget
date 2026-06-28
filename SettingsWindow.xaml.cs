using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClaudeWidget;

public partial class SettingsWindow : Window
{
    private readonly Settings _s;

    // ComboBox (Lunes=0..Domingo=6) <-> DayOfWeek int (Domingo=0..Sábado=6)
    private static readonly int[] ComboToDow = { 1, 2, 3, 4, 5, 6, 0 };
    private static readonly int[] DowToCombo = { 6, 0, 1, 2, 3, 4, 5 };

    public SettingsWindow(Settings settings)
    {
        InitializeComponent();
        _s = settings;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        LoadFrom(_s);
    }

    private void LoadFrom(Settings s)
    {
        CkModel.IsChecked = s.ShowModel;
        CkTodayTokens.IsChecked = s.ShowTodayTokens;
        CkTodayCost.IsChecked = s.ShowTodayCost;
        CkMonthCost.IsChecked = s.ShowMonthCost;
        CkBlockWindow.IsChecked = s.ShowBlockWindow;
        CkBurnRate.IsChecked = s.ShowBurnRate;
        CkProjection.IsChecked = s.ShowProjection;
        CkPlanApi.IsChecked = s.UsePlanApi;
        CkStartWin.IsChecked = s.StartWithWindows;
        CkLinkClaude.IsChecked = s.LinkToClaude;
        CkOnTop.IsChecked = s.AlwaysOnTop;
        CkBlink.IsChecked = s.BlinkOnWaiting;
        CkSound.IsChecked = s.SoundOnWaiting;
        TxtRefresh.Text = s.UsageRefreshSeconds.ToString();

        int dow = s.WeekResetDow >= 0 && s.WeekResetDow <= 6 ? s.WeekResetDow : 4;
        WeekDayBox.SelectedIndex = DowToCombo[dow];
        WeekTimeBox.Text = $"{s.WeekResetHour:00}:{s.WeekResetMinute:00}";

        foreach (ComboBoxItem item in CmbPlan.Items)
            if ((string)item.Content == s.Plan) { CmbPlan.SelectedItem = item; break; }
        if (CmbPlan.SelectedItem == null) CmbPlan.SelectedIndex = 1; // Max 5x
    }

    private static (int h, int m)? ParseTime(string text)
    {
        text = text.Trim().Replace('.', ':');
        var parts = text.Split(':');
        if (parts.Length >= 1 && int.TryParse(parts[0], out var h) && h >= 0 && h <= 23)
        {
            int m = 0;
            if (parts.Length >= 2) int.TryParse(parts[1], out m);
            if (m < 0 || m > 59) m = 0;
            return (h, m);
        }
        return null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _s.ShowModel = CkModel.IsChecked == true;
        _s.ShowTodayTokens = CkTodayTokens.IsChecked == true;
        _s.ShowTodayCost = CkTodayCost.IsChecked == true;
        _s.ShowMonthCost = CkMonthCost.IsChecked == true;
        _s.ShowBlockWindow = CkBlockWindow.IsChecked == true;
        _s.ShowBurnRate = CkBurnRate.IsChecked == true;
        _s.ShowProjection = CkProjection.IsChecked == true;
        _s.UsePlanApi = CkPlanApi.IsChecked == true;
        _s.StartWithWindows = CkStartWin.IsChecked == true;
        _s.LinkToClaude = CkLinkClaude.IsChecked == true;
        _s.AlwaysOnTop = CkOnTop.IsChecked == true;
        _s.BlinkOnWaiting = CkBlink.IsChecked == true;
        _s.SoundOnWaiting = CkSound.IsChecked == true;

        if (int.TryParse(TxtRefresh.Text.Trim(), out var secs) && secs >= 15 && secs <= 3600)
            _s.UsageRefreshSeconds = secs;

        int idx = WeekDayBox.SelectedIndex < 0 ? 3 : WeekDayBox.SelectedIndex;
        _s.WeekResetDow = ComboToDow[idx];
        var t = ParseTime(WeekTimeBox.Text);
        if (t != null) { _s.WeekResetHour = t.Value.h; _s.WeekResetMinute = t.Value.m; }

        if (CmbPlan.SelectedItem is ComboBoxItem it) _s.Plan = (string)it.Content;

        _s.Save();
        Autostart.Apply(_s.StartWithWindows);   // aplicar arranque automático
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
