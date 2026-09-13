using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace DesktopTool.Pages;

/// <summary>
/// pip 包信息。
/// </summary>
public class PipPackage
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>
/// 包管理页面：针对某个 Python 解释器查看 / 安装 / 升级 / 卸载 pip 包，支持选择 pip 源。
/// </summary>
public sealed partial class PackageManagerPage : Page
{
    private PythonEnvironment? _env;
    private readonly ObservableCollection<PipPackage> _packages = new();
    private readonly List<PipPackage> _allPackages = new();
    private bool _busy;

    public PackageManagerPage()
    {
        InitializeComponent();
        SourceCombo.ItemsSource = PipSources.All;
        PackageList.ItemsSource = _packages;
        Loaded += PackageManagerPage_Loaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is PythonEnvironment env)
        {
            _env = env;
            EnvNameText.Text = env.DisplayName;
            EnvPathText.Text = env.Path;
        }
    }

    private void PackageManagerPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 恢复上次选择的 pip 源
        var lastUrl = SettingsStore.GetPipSource();
        SourceCombo.SelectedItem = PipSources.All.FirstOrDefault(s => s.Url == lastUrl) ?? PipSources.All[0];

        _ = RefreshPackagesAsync();
    }

    private string SelectedSourceUrl
        => (SourceCombo.SelectedItem as PipSource)?.Url ?? PipSources.All[0].Url;

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceCombo.SelectedItem is PipSource src)
            SettingsStore.SetPipSource(src.Url);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        RefreshBtn.IsEnabled = !busy;
        InstallBtn.IsEnabled = !busy;
    }

    private void Log(string line)
    {
        LogBox.Text += line + Environment.NewLine;
        LogBox.SelectionStart = LogBox.Text.Length;
        LogBox.SelectionLength = 0;
    }

    /// <summary>执行 python 命令，流式输出到回调，返回完整输出与退出码。</summary>
    private async Task<(string Output, int ExitCode)> RunProcessAsync(string args, Action<string>? onOutput = null)
    {
        if (_env is null) return ("", -1);

        var psi = new ProcessStartInfo
        {
            FileName = _env.Path,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // 强制 Python 以 UTF-8 输出，避免中文环境下 GBK 输出被误解码成乱码
        psi.Environment["PYTHONUTF8"] = "1";

        using var proc = Process.Start(psi);
        if (proc is null) return ("", -1);

        var sb = new StringBuilder();

        async Task Drain(StreamReader reader)
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null) break;
                sb.AppendLine(line);
                if (onOutput is not null)
                    DispatcherQueue.TryEnqueue(() => onOutput(line));
            }
        }

        await Task.WhenAll(Drain(proc.StandardOutput), Drain(proc.StandardError), proc.WaitForExitAsync());
        return (sb.ToString(), proc.ExitCode);
    }

    private async Task RefreshPackagesAsync()
    {
        SetBusy(true);
        LogBox.Text = "";
        Log("正在读取已安装包…");

        var (output, _) = await RunProcessAsync("-m pip list --format=json");

        _allPackages.Clear();
        _packages.Clear();
        try
        {
            using var doc = JsonDocument.Parse(output);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.GetProperty("name").GetString() ?? "";
                var ver = el.GetProperty("version").GetString() ?? "";
                if (name.Length > 0)
                    _allPackages.Add(new PipPackage { Name = name, Version = ver });
            }
            Log($"共 {_allPackages.Count} 个包");
        }
        catch
        {
            Log("解析包列表失败：" + (output.Length > 200 ? output[..200] : output));
        }

        ApplyFilter();
        SetBusy(false);
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        _packages.Clear();
        foreach (var p in _allPackages)
        {
            if (q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                _packages.Add(p);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshPackagesAsync();

    private async void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        var pkg = InstallBox.Text.Trim();
        if (pkg.Length == 0 || _busy) return;
        InstallBox.Text = "";
        await RunPipAction($"安装 {pkg}", $"-m pip install \"{pkg}\" -i {SelectedSourceUrl}");
    }

    private void InstallBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            InstallBtn_Click(InstallBtn, new RoutedEventArgs());
        }
    }

    private async void UpgradeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is PipPackage pkg && !_busy)
            await RunPipAction($"升级 {pkg.Name}", $"-m pip install --upgrade \"{pkg.Name}\" -i {SelectedSourceUrl}");
    }

    private async void UninstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is PipPackage pkg && !_busy)
            await RunPipAction($"卸载 {pkg.Name}", $"-m pip uninstall -y \"{pkg.Name}\"");
    }

    private async Task RunPipAction(string actionName, string args)
    {
        SetBusy(true);
        LogBox.Text = "";
        Log($"$ python {args}");
        var (_, exitCode) = await RunProcessAsync(args, Log);
        Log(exitCode == 0 ? $"{actionName} 成功" : $"{actionName} 失败（退出码 {exitCode}）");
        SetBusy(false);
        await RefreshPackagesAsync();
    }
}
