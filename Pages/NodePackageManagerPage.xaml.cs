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
/// npm / yarn / pnpm 包信息。
/// </summary>
public class NpmPackage
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>
/// 包管理页面：支持两种模式——
/// ① 全局模式：针对某个 Node 运行时管理全局包；
/// ② 项目模式：针对某个项目文件夹（含 package.json）管理其依赖，node.exe 自动从 PATH 查找。
///
/// 包列表直接扫描 node_modules 目录（与包管理器无关，反映实际安装）；
/// 包管理器（npm / yarn / pnpm）仅用于执行安装 / 升级 / 卸载操作；
/// 切换包管理器时若环境缺失，只提示并给出"安装"按钮，由用户手动触发。
/// </summary>
public sealed partial class NodePackageManagerPage : Page
{
    private static readonly string[] PmNames = { "npm", "yarn", "pnpm" };

    private NodeEnvironment? _env;
    private NodeProject? _project;
    private string _pmName = "npm";
    private readonly ObservableCollection<NpmPackage> _packages = new();
    private readonly List<NpmPackage> _allPackages = new();
    private bool _busy;

    public NodePackageManagerPage()
    {
        InitializeComponent();
        PmCombo.ItemsSource = PmNames;
        SourceCombo.ItemsSource = NodeSources.All;
        PackageList.ItemsSource = _packages;
        Loaded += NodePackageManagerPage_Loaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is NodeEnvironment env)
        {
            _env = env;
            _project = null;
            PageTitleText.Text = "包管理（全局）";
            EnvNameText.Text = $"全局：{env.DisplayName}";
            EnvPathText.Text = env.Path;
        }
        else if (e.Parameter is NodeProject proj)
        {
            _project = proj;
            _env = null;
            PageTitleText.Text = "包管理（项目）";
            EnvNameText.Text = $"项目：{proj.DisplayName}";
            EnvPathText.Text = proj.Path;
        }
    }

    private bool IsProjectMode => _project is not null;

    private async void NodePackageManagerPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 恢复上次选择的 npm 源
        var lastUrl = SettingsStore.GetNpmSource();
        SourceCombo.SelectedItem = NodeSources.All.FirstOrDefault(s => s.Url == lastUrl) ?? NodeSources.All[0];

        // 设置包管理器（npm 一定有；不触发列表刷新）
        PmCombo.SelectedItem = "npm";

        // 首次加载包列表
        await RefreshPackagesAsync();
    }

    private string SelectedSourceUrl
        => (SourceCombo.SelectedItem as NodeSource)?.Url ?? NodeSources.All[0].Url;

    // =====================================================================
    // node.exe 定位与包管理器入口
    // =====================================================================

    /// <summary>当前模式下使用的 node.exe：项目模式自动从 PATH / 工具托管目录查找，全局模式用所选运行时。</summary>
    private string? GetNodeExe() => IsProjectMode ? FindNodeExe() : _env?.Path;

    private static string? FindNodeExe()
    {
        var toolNode = ToolPaths.NodeExe;
        if (File.Exists(toolNode)) return toolNode;

        var whereNode = FirstLine(RunCommand("where", "node"));
        if (!string.IsNullOrEmpty(whereNode) && File.Exists(whereNode)) return whereNode;
        return null;
    }

    /// <summary>npm 全局前缀目录（npm prefix -g）。</summary>
    private string? GetNpmGlobalPrefix()
    {
        var nodeExe = GetNodeExe();
        if (string.IsNullOrEmpty(nodeExe)) return null;
        var nodeDir = Path.GetDirectoryName(nodeExe);
        if (string.IsNullOrEmpty(nodeDir)) return null;
        var npmCli = Path.Combine(nodeDir, "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(npmCli)) return null;

        var psi = new ProcessStartInfo
        {
            FileName = nodeExe,
            Arguments = $"\"{npmCli}\" prefix -g",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(12000);
            var line = output.Trim();
            return line.Length == 0 ? null : line;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取包管理器的可执行入口（node.exe + cli.js 方式，避免 .cmd 重定向问题）。</summary>
    private (string Exe, string ArgsPrefix)? GetPmEntry(string pm)
    {
        var nodeExe = GetNodeExe();
        if (string.IsNullOrEmpty(nodeExe)) return null;
        var nodeDir = Path.GetDirectoryName(nodeExe);
        if (string.IsNullOrEmpty(nodeDir)) return null;

        if (pm == "npm")
        {
            var npmCli = Path.Combine(nodeDir, "node_modules", "npm", "bin", "npm-cli.js");
            if (File.Exists(npmCli))
                return (nodeExe, $"\"{npmCli}\"");
            return ("npm", "");
        }

        var prefix = GetNpmGlobalPrefix();
        if (string.IsNullOrEmpty(prefix)) return null;

        if (pm == "yarn")
        {
            var yarnJs = Path.Combine(prefix, "node_modules", "yarn", "bin", "yarn.js");
            if (File.Exists(yarnJs)) return (nodeExe, $"\"{yarnJs}\"");
            return ("yarn", "");
        }
        if (pm == "pnpm")
        {
            var pnpmCjs = Path.Combine(prefix, "node_modules", "pnpm", "bin", "pnpm.cjs");
            if (File.Exists(pnpmCjs)) return (nodeExe, $"\"{pnpmCjs}\"");
            return ("pnpm", "");
        }
        return null;
    }

    /// <summary>判断当前环境是否已安装指定包管理器（yarn / pnpm）。</summary>
    private bool HasPm(string pm)
    {
        if (pm == "npm") return true;
        var prefix = GetNpmGlobalPrefix();
        if (string.IsNullOrEmpty(prefix)) return false;
        return Directory.Exists(Path.Combine(prefix, "node_modules", pm));
    }

    /// <summary>列表对应的 node_modules 目录（项目模式 = 项目目录；全局模式 = npm 全局 prefix）。</summary>
    private string? GetNodeModulesDir()
    {
        if (IsProjectMode)
            return Path.Combine(_project!.Path, "node_modules");
        var prefix = GetNpmGlobalPrefix();
        if (string.IsNullOrEmpty(prefix)) return null;
        return Path.Combine(prefix, "node_modules");
    }

    // =====================================================================
    // 包列表（扫描 node_modules，与包管理器无关）
    // =====================================================================

    private static List<NpmPackage> ScanNodeModules(string nodeModulesDir)
    {
        var result = new List<NpmPackage>();
        if (!Directory.Exists(nodeModulesDir)) return result;

        foreach (var dir in Directory.GetDirectories(nodeModulesDir))
        {
            var name = Path.GetFileName(dir);

            // 跳过隐藏/内部目录（.bin、.pnpm、.cache 等）与以 . 开头的内容
            if (name.StartsWith(".") || name.StartsWith("_")) continue;

            if (name.StartsWith("@"))
            {
                // scoped 包：@scope/name，二级目录
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var pkgName = $"{name}/{Path.GetFileName(sub)}";
                    result.Add(new NpmPackage
                    {
                        Name = pkgName,
                        Version = ReadPackageVersion(Path.Combine(sub, "package.json")),
                    });
                }
            }
            else
            {
                result.Add(new NpmPackage
                {
                    Name = name,
                    Version = ReadPackageVersion(Path.Combine(dir, "package.json")),
                });
            }
        }
        return result;
    }

    private static string ReadPackageVersion(string packageJsonPath)
    {
        try
        {
            if (!File.Exists(packageJsonPath)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        }
        catch
        {
        }
        return "";
    }

    // =====================================================================
    // 命令构造（按模式 + 包管理器，仅用于操作）
    // =====================================================================

    private string BuildInstallCmd(string pkg)
    {
        if (IsProjectMode)
        {
            return _pmName switch
            {
                "yarn" => $"add \"{pkg}\" --registry {SelectedSourceUrl}",
                "pnpm" => $"add \"{pkg}\" --registry={SelectedSourceUrl}",
                _ => $"install \"{pkg}\" --registry={SelectedSourceUrl}",
            };
        }
        return _pmName switch
        {
            "yarn" => $"global add \"{pkg}\" --registry {SelectedSourceUrl}",
            "pnpm" => $"add -g \"{pkg}\" --registry={SelectedSourceUrl}",
            _ => $"install -g \"{pkg}\" --registry={SelectedSourceUrl}",
        };
    }

    private string BuildUpgradeCmd(string pkg)
    {
        if (IsProjectMode)
        {
            return _pmName switch
            {
                "yarn" => $"upgrade \"{pkg}\" --registry {SelectedSourceUrl}",
                "pnpm" => $"update \"{pkg}\" --registry={SelectedSourceUrl}",
                _ => $"update \"{pkg}\" --registry={SelectedSourceUrl}",
            };
        }
        return _pmName switch
        {
            "yarn" => $"global upgrade \"{pkg}\" --registry {SelectedSourceUrl}",
            "pnpm" => $"update -g \"{pkg}\" --registry={SelectedSourceUrl}",
            _ => $"update -g \"{pkg}\" --registry={SelectedSourceUrl}",
        };
    }

    private string BuildUninstallCmd(string pkg)
    {
        if (IsProjectMode)
        {
            return _pmName switch
            {
                "yarn" => $"remove \"{pkg}\"",
                "pnpm" => $"remove \"{pkg}\"",
                _ => $"uninstall \"{pkg}\"",
            };
        }
        return _pmName switch
        {
            "yarn" => $"global remove \"{pkg}\"",
            "pnpm" => $"remove -g \"{pkg}\"",
            _ => $"uninstall -g \"{pkg}\"",
        };
    }

    // =====================================================================
    // 执行
    // =====================================================================

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceCombo.SelectedItem is NodeSource src)
            SettingsStore.SetNpmSource(src.Url);
    }

    private void PmCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PmCombo.SelectedItem is not string pm) return;

        _pmName = pm;

        // 包管理器缺失 → 只提示 + 显示安装按钮，不自动安装、不刷新列表
        if (!HasPm(pm))
        {
            PmStatusText.Text = $"当前环境未安装 {pm}（列表仍正常显示 node_modules 已安装的包）";
            InstallPmBtn.Content = $"安装 {pm}";
            PmStatusPanel.Visibility = Visibility.Visible;
        }
        else
        {
            PmStatusPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void InstallPmBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var pm = _pmName;
        await RunPmAction("npm", $"安装 {pm}", $"install -g \"{pm}\" --registry={SelectedSourceUrl}");
        if (HasPm(pm)) PmStatusPanel.Visibility = Visibility.Collapsed;
        await RefreshPackagesAsync();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        RefreshBtn.IsEnabled = !busy;
        InstallBtn.IsEnabled = !busy;
        PmCombo.IsEnabled = !busy;
        SourceCombo.IsEnabled = !busy;
    }

    private void Log(string line)
    {
        LogBox.Text += line + Environment.NewLine;
        LogBox.SelectionStart = LogBox.Text.Length;
        LogBox.SelectionLength = 0;
    }

    /// <summary>执行包管理器命令，流式输出到回调，返回完整输出与退出码。</summary>
    private async Task<(string Output, int ExitCode)> RunProcessAsync(string pm, string args, Action<string>? onOutput = null)
    {
        var entry = GetPmEntry(pm);
        if (entry is null) return ("", -1);

        var psi = new ProcessStartInfo
        {
            FileName = entry.Value.Exe,
            Arguments = entry.Value.ArgsPrefix.Length > 0 ? $"{entry.Value.ArgsPrefix} {args}" : args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // 项目模式：在项目目录下执行
        if (IsProjectMode)
            psi.WorkingDirectory = _project!.Path;

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

        var nodeModulesDir = GetNodeModulesDir();
        if (nodeModulesDir is null)
        {
            Log(IsProjectMode ? "未找到项目 node_modules 目录" : "未找到全局包目录（npm prefix -g 不可用）");
            _allPackages.Clear();
            _packages.Clear();
            SetBusy(false);
            return;
        }

        Log($"扫描 {nodeModulesDir}");
        _allPackages.Clear();
        _packages.Clear();
        await Task.Run(() => _allPackages.AddRange(ScanNodeModules(nodeModulesDir)));
        Log($"共 {_allPackages.Count} 个包");
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
        await RunPmAction(_pmName, $"安装 {pkg}", BuildInstallCmd(pkg));
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
        if (sender is Button btn && btn.Tag is NpmPackage pkg && !_busy)
            await RunPmAction(_pmName, $"升级 {pkg.Name}", BuildUpgradeCmd(pkg.Name));
    }

    private async void UninstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NpmPackage pkg && !_busy)
            await RunPmAction(_pmName, $"卸载 {pkg.Name}", BuildUninstallCmd(pkg.Name));
    }

    private async Task RunPmAction(string pm, string actionName, string args)
    {
        SetBusy(true);
        LogBox.Text = "";
        Log($"$ {pm} {args}");
        var (_, exitCode) = await RunProcessAsync(pm, args, Log);
        Log(exitCode == 0 ? $"{actionName} 成功" : $"{actionName} 失败（退出码 {exitCode}）");
        SetBusy(false);
        await RefreshPackagesAsync();
    }

    private static string? RunCommand(string fileName, string args) => ProcessHelper.Run(fileName, args);

    private static string FirstLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        return output.Split('\n')[0].Trim();
    }
}
