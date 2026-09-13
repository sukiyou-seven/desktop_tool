using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopTool.Pages;

/// <summary>
/// 下载安装 Conda 页面：通过官方 Miniconda 安装器静默安装。
/// </summary>
public sealed partial class DownloadCondaPage : Page
{
    private const string DownloadUrl = "https://repo.anaconda.com/miniconda/Miniconda3-latest-Windows-x86_64.exe";

    private bool _busy;

    public DownloadCondaPage()
    {
        InitializeComponent();
        Loaded += DownloadCondaPage_Loaded;
    }

    private static string InstallDir => ToolPaths.InstallConda;

    private async void DownloadCondaPage_Loaded(object sender, RoutedEventArgs e)
    {
        InstallDirText.Text = $"安装目录：{InstallDir}";
        await RefreshStatusAsync();
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void Log(string line)
    {
        LogBox.Text += line + Environment.NewLine;
        LogBox.SelectionStart = LogBox.Text.Length;
        LogBox.SelectionLength = 0;
    }

    private async Task RefreshStatusAsync()
    {
        var condaPath = Path.Combine(InstallDir, "Scripts", "conda.exe");
        // 后台检测，避免阻塞 UI
        var (managedVersion, pathConda, condaVersion) = await Task.Run(() =>
        {
            if (File.Exists(condaPath))
            {
                var v = RunCommand(condaPath, "--version");
                return (v, "", "");
            }
            var pc = FirstLine(RunCommand("where", "conda"));
            var cv = RunCommand("conda", "--version");
            return ("", pc, cv);
        });

        if (!string.IsNullOrEmpty(managedVersion))
        {
            CondaVersionText.Text = string.IsNullOrEmpty(managedVersion) ? "已安装（版本未知）" : managedVersion;
            InstalledBadgeBorder.Visibility = Visibility.Visible;
            UninstallBtn.Visibility = Visibility.Visible;
        }
        else if (!string.IsNullOrEmpty(condaVersion))
        {
            CondaVersionText.Text = $"已检测到：{condaVersion}  ({pathConda})";
            InstalledBadgeBorder.Visibility = Visibility.Visible;
            UninstallBtn.Visibility = Visibility.Visible;
        }
        else
        {
            CondaVersionText.Text = "未检测到 conda（可点击下方按钮下载安装）";
            InstalledBadgeBorder.Visibility = Visibility.Collapsed;
            UninstallBtn.Visibility = Visibility.Collapsed;
        }
    }

    private async void UninstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var dialog = new ContentDialog
        {
            Title = "卸载 Conda",
            Content = $"将删除 Miniconda 安装目录：\n{InstallDir}\n\n此操作不可恢复。",
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _busy = true;
        UninstallBtn.IsEnabled = false;
        InstallBtn.IsEnabled = false;
        LogBox.Text = "";
        try
        {
            if (Directory.Exists(InstallDir))
                Directory.Delete(InstallDir, recursive: true);
            Log($"已删除安装目录 {InstallDir}");
            Log("已卸载 Conda");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Log($"卸载失败：{ex.Message}");
        }
        finally
        {
            _busy = false;
            UninstallBtn.IsEnabled = true;
            InstallBtn.IsEnabled = true;
        }
    }

    private async void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await DownloadAndInstallAsync();
    }

    private async Task DownloadAndInstallAsync()
    {
        _busy = true;
        InstallBtn.IsEnabled = false;
        LogBox.Text = "";

        var installersDir = ToolPaths.DownloadsConda;
        Directory.CreateDirectory(installersDir);
        var dest = Path.Combine(installersDir, "Miniconda3-latest-Windows-x86_64.exe");

        try
        {
            // 1. 下载安装器
            Log($"下载 {DownloadUrl}");
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(20);
            using (var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var content = await response.Content.ReadAsStreamAsync();
                await using var fs = File.Create(dest);

                var buffer = new byte[81920];
                long read = 0;
                long lastPct = -1;
                int n;
                while ((n = await content.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    if (total > 0)
                    {
                        var pct = read * 100 / total;
                        if (pct >= lastPct + 5)
                        {
                            lastPct = pct;
                            Log($"下载中 {pct}%");
                        }
                    }
                }
            }
            Log($"下载完成：{dest}");

            // 2. 静默安装（用户级，/D 参数需放在最后且不带引号）
            Log($"开始静默安装到 {InstallDir} …");
            var psi = new ProcessStartInfo
            {
                FileName = dest,
                Arguments = $"/S /InstallationType=JustMe /AddToPath=0 /RegisterPython=0 /D={InstallDir}",
                UseShellExecute = true,
            };
            var proc = Process.Start(psi);
            if (proc is null)
            {
                Log("安装器启动失败");
                return;
            }
            await proc.WaitForExitAsync();
            Log(proc.ExitCode == 0 ? "安装完成" : $"安装器退出码 {proc.ExitCode}（可能安装失败）");

            // 3. 刷新状态
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Log($"操作失败：{ex.Message}");
        }
        finally
        {
            _busy = false;
            InstallBtn.IsEnabled = true;
        }
    }

    private static string? RunCommand(string fileName, string args) => ProcessHelper.Run(fileName, args);

    private static string FirstLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        return output.Split('\n')[0].Trim();
    }
}
