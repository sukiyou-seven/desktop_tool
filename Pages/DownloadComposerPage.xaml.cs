using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopTool.Pages;

/// <summary>
/// 下载安装 Composer 页面：从 getcomposer.org 官方源下载 composer.phar 到工具托管目录。
/// </summary>
public sealed partial class DownloadComposerPage : Page
{
    private const string ComposerUrl = "https://getcomposer.org/download/latest-stable/composer.phar";

    private static string ToolComposerFile => ToolPaths.ComposerFile;

    private bool _busy;

    public DownloadComposerPage()
    {
        InitializeComponent();
        Loaded += DownloadComposerPage_Loaded;
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private async void DownloadComposerPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 后台检测，避免阻塞 UI
        var where = await Task.Run(() => FirstLine(RunCommand("where", "composer")));
        var managedExists = await Task.Run(() => File.Exists(ToolComposerFile));
        UpdateStatus(where, managedExists);
    }

    private void UpdateStatus(string where, bool managedExists)
    {
        UninstallBtn.Visibility = managedExists ? Visibility.Visible : Visibility.Collapsed;
        if (managedExists)
        {
            StatusText.Text = $"工具托管：{ToolComposerFile}\nPATH 中另有：{(string.IsNullOrEmpty(where) ? "无" : where)}";
            InstallBtn.Content = "更新";
        }
        else if (!string.IsNullOrEmpty(where))
        {
            StatusText.Text = $"检测到 PATH 中的 Composer：{where}\n也可以安装到工具托管目录统一管理";
            InstallBtn.Content = "安装";
        }
        else
        {
            StatusText.Text = "未检测到 Composer";
            InstallBtn.Content = "安装";
        }
    }

    private async void UninstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var dialog = new ContentDialog
        {
            Title = "卸载 Composer",
            Content = $"将删除工具托管的文件：\n{ToolComposerFile}\n\n此操作不可恢复。",
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true);
        LogBox.Text = "";
        try
        {
            if (File.Exists(ToolComposerFile))
                File.Delete(ToolComposerFile);
            Log("已卸载工具托管的 Composer");

            var where = await Task.Run(() => FirstLine(RunCommand("where", "composer")));
            var managedExists = await Task.Run(() => File.Exists(ToolComposerFile));
            UpdateStatus(where, managedExists);
        }
        catch (Exception ex)
        {
            Log($"卸载失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        InstallBtn.IsEnabled = !busy;
    }

    private void Log(string line)
    {
        LogBox.Text += line + Environment.NewLine;
        LogBox.SelectionStart = LogBox.Text.Length;
        LogBox.SelectionLength = 0;
    }

    private async void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        LogBox.Text = "";
        try
        {
            var dir = Path.GetDirectoryName(ToolComposerFile)!;
            Directory.CreateDirectory(dir);

            Log($"下载 {ComposerUrl}");
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            var data = await client.GetByteArrayAsync(ComposerUrl);
            if (data.Length < 1024)
            {
                Log("下载内容异常（文件过小），可能失败");
                return;
            }

            File.WriteAllBytes(ToolComposerFile, data);
            Log($"已保存到 {ToolComposerFile}（{data.Length / 1024} KB）");
            Log("使用方式：php composer.phar <命令>（本工具会优先使用托管/PATH 中的 PHP）");

            var where = await Task.Run(() => FirstLine(RunCommand("where", "composer")));
            UpdateStatus(where, managedExists: true);
        }
        catch (Exception ex)
        {
            Log($"操作失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string? RunCommand(string fileName, string args) => ProcessHelper.Run(fileName, args);

    private static string FirstLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        return output.Split('\n')[0].Trim();
    }
}
