using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using BG3ModSync.Models;

namespace BG3ModSync.Views;

public partial class ExcludeWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly AppSettings _settings;
    private readonly Action<string> _logCallback;

    private readonly Dictionary<string, ObservableCollection<string>> _categoryRules = new(StringComparer.OrdinalIgnoreCase);

    public ExcludeWindow(AppSettings settings, Action<string> logCallback)
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int useDark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            }
            catch { }
        };

        _settings = settings;
        _logCallback = logCallback;

        _categoryRules["PAK"] = new ObservableCollection<string>(_settings.ExcludedPakRules ?? AppSettings.GetDefaultPakExclusions());
        _categoryRules["JSON"] = new ObservableCollection<string>(_settings.ExcludedJsonRules ?? AppSettings.GetDefaultJsonExclusions());
        _categoryRules["GAMEDATA"] = new ObservableCollection<string>(_settings.ExcludedGamedataRules ?? AppSettings.GetDefaultGamedataExclusions());
        _categoryRules["LOADORDER"] = new ObservableCollection<string>(_settings.ExcludedLoadOrderRules ?? AppSettings.GetDefaultLoadOrderExclusions());

        UpdateCategoryView();
    }

    private string CurrentCategoryKey
    {
        get
        {
            if (CatJsonRadio?.IsChecked == true) return "JSON";
            if (CatGamedataRadio?.IsChecked == true) return "GAMEDATA";
            if (CatLoadOrderRadio?.IsChecked == true) return "LOADORDER";
            return "PAK";
        }
    }

    private void CategoryRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateCategoryView();
    }

    private void UpdateCategoryView()
    {
        string key = CurrentCategoryKey;
        if (!_categoryRules.TryGetValue(key, out var list))
            return;

        RulesListBox.ItemsSource = list;

        switch (key)
        {
            case "PAK":
                ListHeaderText.Text = $"Excluded in PAK — Mods folder ({list.Count}):";
                CategoryHintText.Text = "Match file name (english.pak), extension (.txt, .log), or part of name (backup).";
                break;
            case "JSON":
                ListHeaderText.Text = $"Excluded in JSON — Script Extender ({list.Count}):";
                CategoryHintText.Text = "Match folder name (Armory, CC_Preset, CPF), file name, or part of name.";
                break;
            case "GAMEDATA":
                ListHeaderText.Text = $"Excluded in GAMEDATA — \\bin & NativeMods ({list.Count}):";
                CategoryHintText.Text = "Match file name, extension (.log, .bak, .tmp), or part of name (vortex_backup).";
                break;
            case "LOADORDER":
                ListHeaderText.Text = $"Excluded in LOADORDER — PlayerProfiles ({list.Count}):";
                CategoryHintText.Text = "Add 'modsettings.lsx' here if you do not want Load Order sent or overwritten.";
                break;
        }
    }

    private void RuleInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddRuleBtn_Click(sender, e);
            e.Handled = true;
        }
    }

    private void AddRuleBtn_Click(object sender, RoutedEventArgs e)
    {
        string raw = RuleInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            RuleInputBox.Focus();
            return;
        }

        var list = _categoryRules[CurrentCategoryKey];
        // Support comma/semicolon separated batch input
        var parts = raw.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            string item = part.Trim();
            if (!string.IsNullOrEmpty(item) && !list.Any(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(item);
            }
        }

        RuleInputBox.Clear();
        RuleInputBox.Focus();
        UpdateCategoryView();
    }

    private void RemoveRuleBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = RulesListBox.SelectedItems.Cast<string>().ToList();
        if (selected.Count == 0) return;

        var list = _categoryRules[CurrentCategoryKey];
        foreach (var item in selected)
        {
            list.Remove(item);
        }
        UpdateCategoryView();
    }

    private void ResetCategoryDefaultBtn_Click(object sender, RoutedEventArgs e)
    {
        string key = CurrentCategoryKey;
        List<string> defaults = key switch
        {
            "PAK" => AppSettings.GetDefaultPakExclusions(),
            "JSON" => AppSettings.GetDefaultJsonExclusions(),
            "GAMEDATA" => AppSettings.GetDefaultGamedataExclusions(),
            "LOADORDER" => AppSettings.GetDefaultLoadOrderExclusions(),
            _ => new List<string>()
        };

        var list = _categoryRules[key];
        list.Clear();
        foreach (var d in defaults)
        {
            list.Add(d);
        }
        UpdateCategoryView();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        // If user typed something in RuleInputBox and clicked Save directly, add it first
        if (!string.IsNullOrWhiteSpace(RuleInputBox.Text))
        {
            AddRuleBtn_Click(sender, e);
        }

        _settings.ExcludedPakRules = _categoryRules["PAK"].ToList();
        _settings.ExcludedJsonRules = _categoryRules["JSON"].ToList();
        _settings.ExcludedGamedataRules = _categoryRules["GAMEDATA"].ToList();
        _settings.ExcludedLoadOrderRules = _categoryRules["LOADORDER"].ToList();
        _settings.Save();

        _logCallback($"[EXCLUDE] Saved exclude rules (PAK: {_settings.ExcludedPakRules.Count}, JSON: {_settings.ExcludedJsonRules.Count}, GAMEDATA: {_settings.ExcludedGamedataRules.Count}, LOADORDER: {_settings.ExcludedLoadOrderRules.Count}).");
        DialogResult = true;
        Close();
    }
}
