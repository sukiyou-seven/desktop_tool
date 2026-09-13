using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DesktopTool.Pages;

/// <summary>
/// 服务器目录浏览：显示当前路径下的子目录，可双击进入 / 上级 / 直接输入路径。
/// 返回的 CurrentPath 即用户最终确认的目录。
/// </summary>
public sealed partial class ServerDirBrowserDialog : StackPanel
{
    private readonly ServerInfo _server;
    public string CurrentPath { get; private set; }

    public ObservableCollection<string> SubDirs { get; } = new();

    public ServerDirBrowserDialog(ServerInfo server, string startPath, List<string> initialDirs)
    {
        InitializeComponent();
        _server = server;
        CurrentPath = UploadEngine.NormalizeRemoteDir(startPath);
        UpdatePathUi();
        SubDirs.Clear();
        foreach (var d in initialDirs) SubDirs.Add(d);
    }

    private void UpdatePathUi()
    {
        PathText.Text = CurrentPath == "" ? "/" : CurrentPath;
        PathBox.Text = CurrentPath == "" ? "/" : CurrentPath;
    }

    private async Task LoadDirsAsync(string dir)
    {
        try
        {
            var dirs = await Task.Run(() => UploadEngine.SftpListDirs(_server, dir));
            CurrentPath = UploadEngine.NormalizeRemoteDir(dir);
            UpdatePathUi();
            SubDirs.Clear();
            foreach (var d in dirs) SubDirs.Add(d);
        }
        catch (Exception ex)
        {
            var dlg = new ContentDialog
            {
                Title = "服务器目录",
                Content = "读取失败：" + ex.Message,
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot,
            };
            await dlg.ShowAsync();
        }
    }

    private async void DirList_ItemClick(object sender, ItemClickEventArgs e)
        => await EnterAsync(e.ClickedItem as string);

    private async Task EnterAsync(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        var basePath = CurrentPath == "" ? "" : CurrentPath;
        await LoadDirsAsync(basePath.TrimEnd('/') + "/" + dir.Trim('/'));
    }

    private async void UpBtn_Click(object sender, RoutedEventArgs e)
    {
        var cur = CurrentPath.Trim('/');
        var idx = cur.LastIndexOf('/');
        var parent = idx < 0 ? "" : cur[..idx];
        await LoadDirsAsync(parent == "" ? "/" : "/" + parent);
    }

    private async void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var path = PathBox.Text.Trim();
            if (!string.IsNullOrEmpty(path))
                await LoadDirsAsync(path);
        }
    }
}
