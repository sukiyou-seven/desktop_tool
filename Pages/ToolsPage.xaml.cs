using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace DesktopTool.Pages;

/// <summary>
/// 工具管理页：已安装（注册表扫描，卡片展示，支持自定义排序）/ 未安装（winget 安装，列表展示）。
/// </summary>
public sealed partial class ToolsPage : Page
{
    private ToolCatalog _catalog = new();
    private List<InstalledApp> _installed = new();
    private bool _wingetAvailable;

    public ToolsPage()
    {
        InitializeComponent();
        Loaded += ToolsPage_Loaded;
    }

    private void ToolsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _catalog = ToolsCatalogStore.Load();
        _wingetAvailable = WingetHelper.IsAvailable();
        SubtitleText.Text = _wingetAvailable
            ? "winget 可用，安装到软件工作目录/apps/installers/"
            : "未检测到 winget（需 Windows 11 或安装 App Installer），将打开官网";

        _ = Task.Run(() =>
        {
            var apps = InstalledApps.Scan();
            DispatcherQueue.TryEnqueue(() =>
            {
                _installed = apps;
                ApplySortAndRefresh();
            });
        });

        RebuildGroups();
        MainTab.SelectedItem = MainTab.Items[0];
    }

    // ---------- 排序 ----------

    /// <summary>从存储读取排序值，应用到列表并刷新 UI。有排序值的按值降序在前，其余按字母升序。</summary>
    private void ApplySortAndRefresh()
    {
        foreach (var app in _installed)
            app.SortOrder = AppSortStore.Get(app.Name);

        _installed = _installed
            .OrderByDescending(a => a.SortOrder ?? int.MinValue)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (MainTab.SelectedItem is SelectorBarItem it && it.Tag as string == "installed")
            InstalledGrid.ItemsSource = _installed;
    }

    // ---------- 主 Tab 切换 ----------

    private void MainTab_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (MainTab.SelectedItem is not SelectorBarItem item) return;
        var tag = item.Tag as string;
        if (tag == "installed")
        {
            InstalledGrid.Visibility = Visibility.Visible;
            AvailablePanel.Visibility = Visibility.Collapsed;
            InstalledGrid.ItemsSource = _installed;
        }
        else
        {
            InstalledGrid.Visibility = Visibility.Collapsed;
            AvailablePanel.Visibility = Visibility.Visible;
        }
    }

    // ---------- 未安装分组 ----------

    private void RebuildGroups()
    {
        GroupCombo.Items.Clear();
        foreach (var group in _catalog.Groups)
            GroupCombo.Items.Add(new ComboBoxItem { Content = group.Name, Tag = group });
        if (GroupCombo.Items.Count > 0)
            GroupCombo.SelectedIndex = 0;
    }

    private void GroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupCombo.SelectedItem is ComboBoxItem it && it.Tag is ToolCatalogGroup group)
            AvailableList.ItemsSource = group.Tools;
    }

    // ---------- 点击已安装卡片启动程序 ----------

    private void InstalledCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is InstalledApp app)
            LaunchApp(app);
    }

    private void InstalledCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            FlyoutBase.ShowAttachedFlyout(fe);
    }

    private static void LaunchApp(InstalledApp app)
    {
        if (!string.IsNullOrEmpty(app.ExePath) && File.Exists(app.ExePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(app.ExePath) { UseShellExecute = true });
                return;
            }
            catch { }
        }
        if (!string.IsNullOrEmpty(app.InstallLocation) && Directory.Exists(app.InstallLocation))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", app.InstallLocation) { UseShellExecute = true });
        }
    }

    // ---------- 右键菜单：设置排序 ----------

    private async void SetSortOrder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is InstalledApp app)
        {
            var textBox = new TextBox
            {
                PlaceholderText = "输入数字，越大越靠前；留空则按字母排序",
                Text = app.SortOrder.HasValue ? app.SortOrder.Value.ToString() : "",
            };

            var dialog = new ContentDialog
            {
                Title = $"设置排序 — {app.Name}",
                Content = textBox,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                XamlRoot = this.XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var text = textBox.Text.Trim();
                if (int.TryParse(text, out var n))
                    AppSortStore.Set(app.Name, n);
                else
                    AppSortStore.Remove(app.Name);
                ApplySortAndRefresh();
            }
        }
    }

    // ---------- 安装 ----------

    private async void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ToolCatalogTool tool) return;
        if (tool.IsBusy) return;

        if (!_wingetAvailable || string.IsNullOrWhiteSpace(tool.WingetId))
        {
            OpenUrl(tool.HomepageUrl);
            return;
        }

        tool.IsBusy = true;
        tool.ProgressText = "准备安装…";
        try
        {
            var progress = new Progress<string>(msg =>
            {
                var lines = msg.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var last = lines.Length > 0 ? lines[^1].Trim() : msg;
                if (last.Length > 60) last = last.Substring(0, 60) + "…";
                tool.ProgressText = last;
            });

            var (ok, output) = await WingetHelper.InstallAsync(tool.WingetId, progress: progress);
            tool.ProgressText = ok ? "安装完成" : $"安装失败：{Truncate(output, 120)}";

            if (ok)
            {
                _ = Task.Run(() =>
                {
                    var apps = InstalledApps.Scan();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _installed = apps;
                        ApplySortAndRefresh();
                    });
                });
            }
        }
        catch (Exception ex)
        {
            tool.ProgressText = $"安装异常：{ex.Message}";
        }
        finally
        {
            tool.IsBusy = false;
        }
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
