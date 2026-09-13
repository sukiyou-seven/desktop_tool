using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DesktopTool.Pages;

/// <summary>AI 平台 API Key 余额监控页。</summary>
public sealed partial class AIMonitorPage : Page
{
    private AiPlatform _currentPlatform = AiPlatform.DeepSeek;
    private bool _initialized;
    private List<KeyRow> _rows = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public AIMonitorPage()
    {
        try
        {
            InitializeComponent();
            _initialized = true;
            LoadKeys();
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SeraphineAITool", "aimonitor_crash.log"),
                    ex.ToString());
            }
            catch { }
            throw;
        }
    }

    // ---------- 数据模型 ----------

    private class KeyRow
    {
        public string Id { get; set; } = "";
        public string PlatformText { get; set; } = "";
        public string Label { get; set; } = "";
        public string MaskedKey { get; set; } = "";
        public string BalanceText { get; set; } = "查询中…";
        public Brush BalanceBrush { get; set; } = new SolidColorBrush(Microsoft.UI.Colors.Gray);
        public string GrantedText { get; set; } = "-";
        public string ToppedUpText { get; set; } = "-";
        public string StatusText { get; set; } = "-";
        public Visibility DetailVisibility { get; set; } = Visibility.Collapsed;
    }

    private class BalanceResult
    {
        public string Total = "";
        public string Granted = "";
        public string ToppedUp = "";
        public string Currency = "";
        public bool Available;
        public string? Error;
    }

    // ---------- 加载 ----------

    private void LoadKeys()
    {
        var keys = AiKeyStore.ByPlatform(_currentPlatform);
        _rows = keys.Select(k => new KeyRow
        {
            Id = k.Id,
            PlatformText = k.Platform.ToString(),
            Label = string.IsNullOrEmpty(k.Label) ? k.Platform.ToString() : k.Label,
            MaskedKey = MaskKey(k.ApiKey),
        }).ToList();
        KeyItemsControl.ItemsSource = _rows;
        _ = RefreshAllBalances();
    }

    private static string MaskKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "(空)";
        if (key.Length <= 8) return new string('*', key.Length);
        return key[..4] + new string('*', Math.Max(4, key.Length - 8)) + key[^4..];
    }

    private static readonly Brush GreenBrush = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
    private static readonly Brush RedBrush = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
    private static readonly Brush GrayBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray);

    private static Brush BrushForBalance(string? total)
    {
        if (double.TryParse(total, out var v))
            return v < 2 ? RedBrush : GreenBrush;
        return GrayBrush;
    }

    private void RefreshList()
    {
        KeyItemsControl.ItemsSource = null;
        KeyItemsControl.ItemsSource = _rows;
    }

    // ---------- 平台切换 ----------

    private void PlatformCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (PlatformCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && Enum.TryParse<AiPlatform>(tag, out var p))
        {
            _currentPlatform = p;
            LoadKeys();
        }
    }

    // ---------- 添加 Key ----------

    private async void AddKeyBtn_Click(object sender, RoutedEventArgs e)
    {
        var labelBox = new TextBox { PlaceholderText = "备注名称（可选）" };
        var keyBox = new TextBox { PlaceholderText = "API Key" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(labelBox);
        panel.Children.Add(keyBox);

        var dialog = new ContentDialog
        {
            Title = $"添加 {_currentPlatform} API Key",
            Content = panel,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(keyBox.Text)) return;

        AiKeyStore.Add(new AiKeyEntry
        {
            Platform = _currentPlatform,
            Label = labelBox.Text.Trim(),
            ApiKey = keyBox.Text.Trim(),
        });
        LoadKeys();
    }

    // ---------- 删除 ----------

    private void DeleteKeyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            AiKeyStore.Remove(id);
            LoadKeys();
        }
    }

    // ---------- 刷新余额 ----------

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAllBalances();
    }

    private async Task RefreshAllBalances()
    {
        foreach (var row in _rows)
        {
            row.BalanceText = "查询中…";
            row.GrantedText = "-";
            row.ToppedUpText = "-";
            row.StatusText = "-";
            row.DetailVisibility = Visibility.Collapsed;
        }
        RefreshList();

        var keys = AiKeyStore.ByPlatform(_currentPlatform);
        foreach (var key in keys)
        {
            var row = _rows.FirstOrDefault(r => r.Id == key.Id);
            if (row is null) continue;
            try
            {
                var result = _currentPlatform switch
                {
                    AiPlatform.DeepSeek => await QueryDeepSeekBalance(key.ApiKey),
                    _ => new BalanceResult { Error = "不支持" },
                };
                if (result.Error is not null)
                {
                    row.BalanceText = result.Error;
                    row.BalanceBrush = GrayBrush;
                }
                else
                {
                    row.BalanceText = $"{result.Total} {result.Currency}";
                    row.BalanceBrush = BrushForBalance(result.Total);
                    row.GrantedText = $"{result.Granted} {result.Currency}";
                    row.ToppedUpText = $"{result.ToppedUp} {result.Currency}";
                    row.StatusText = result.Available ? "可用" : "不可用";
                    row.DetailVisibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                row.BalanceText = $"失败：{ex.Message}";
            }
            RefreshList();
        }
    }

    // ---------- DeepSeek ----------

    private static async Task<BalanceResult> QueryDeepSeekBalance(string apiKey)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
        req.Headers.Add("Authorization", $"Bearer {apiKey}");
        using var resp = await Http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return new BalanceResult { Error = $"HTTP {(int)resp.StatusCode}" };

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var result = new BalanceResult
        {
            Available = root.TryGetProperty("is_available", out var avail) && avail.GetBoolean(),
        };

        if (root.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array
            && infos.GetArrayLength() > 0)
        {
            var info = infos[0];
            result.Currency = info.TryGetProperty("currency", out var c) ? c.GetString() ?? "" : "";
            result.Total = info.TryGetProperty("total_balance", out var t) ? t.GetString() ?? "?" : "?";
            result.Granted = info.TryGetProperty("granted_balance", out var g) ? g.GetString() ?? "?" : "?";
            result.ToppedUp = info.TryGetProperty("topped_up_balance", out var tu) ? tu.GetString() ?? "?" : "?";
        }
        else
        {
            result.Error = "无余额信息";
        }
        return result;
    }
}
