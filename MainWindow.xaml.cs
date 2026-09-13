using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using DesktopTool.Pages;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace DesktopTool;

public sealed partial class MainWindow : Window
{
    private bool _syncingSelection;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // 初始窗口大小 1400 x 830，并在主显示器工作区内居中
        var windowSize = new SizeInt32(1400, 830);
        AppWindow.Resize(windowSize);
        CenterWindow(windowSize);

        // 页面导航变化（含后退）时同步左侧导航高亮
        NavFrame.Navigated += NavFrame_Navigated;

        // 全局热键：HOME 唤起 / END 隐藏到后台
        HotkeyManager.Start(
            DispatcherQueue.GetForCurrentThread(),
            id =>
            {
                if (id == HotkeyManager.HotKeyShow) ShowFromHidden();
                else if (id == HotkeyManager.HotKeyHide) HideToBackground();
            });

        // 窗口关闭前注销热键、清理 PHP 开发服务器等子进程
        Closed += (_, _) =>
        {
            HotkeyManager.Stop();
            PhpEnvPage.KillAllDevServers();
        };
    }

    /// <summary>隐藏窗口（屏幕与任务栏均消失，程序驻留后台，HOME 键唤回）。</summary>
    private void HideToBackground()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
    }

    /// <summary>从隐藏状态唤回并抢占前台焦点。</summary>
    private void ShowFromHidden()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
        // 后台进程直接 SetForegroundWindow 常被系统拦截，先临时置顶再取消，确保拿到焦点
        NativeMethods.SetWindowPos(
            hwnd, new IntPtr(NativeMethods.HWND_TOPMOST), 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.SetForegroundWindow(hwnd);
        NativeMethods.SetWindowPos(
            hwnd, new IntPtr(NativeMethods.HWND_NOTOPMOST), 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
    }

    private void CenterWindow(SizeInt32 size)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var x = area.X + Math.Max(0, (area.Width - size.Width) / 2);
        var y = area.Y + Math.Max(0, (area.Height - size.Height) / 2);
        AppWindow.Move(new PointInt32(x, y));
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

    private void NavFrame_Navigated(object sender, NavigationEventArgs e)
    {
        // 根据当前页面类型匹配导航项；子页面（包管理/下载等）不改变高亮
        string? tag = e.SourcePageType.Name switch
        {
            nameof(PythonEnvPage) => "pythonenv",
            nameof(NodeEnvPage) => "nodeenv",
            nameof(PhpEnvPage) => "phpenv",
            nameof(GoEnvPage) => "goenv",
            nameof(RustEnvPage) => "rustenv",
            nameof(JavaEnvPage) => "javaenv",
            nameof(GitPage) => "git",
            nameof(ToolsPage) => "tools",
            nameof(ServersPage) => "servers",
            nameof(CertPage) => "certs",
            nameof(AlidnsPage) => "alidns",
            nameof(ProxyPage) => "proxy",
            nameof(AIMonitorPage) => "aimonitor",
            nameof(ProjectManagementPage) => "projects",
            nameof(SystemInfoPage) => "systeminfo",
            _ => null,
        };
        if (tag is null) return;

        var item = FindNavItemByTag(tag);
        if (item is not null && !ReferenceEquals(NavView.SelectedItem, item))
        {
            _syncingSelection = true;
            NavView.SelectedItem = item;
            _syncingSelection = false;
        }
    }

    private NavigationViewItem? FindNavItemByTag(string tag)
        => FindNavItemRecursive(NavView.MenuItems, tag);

    private static NavigationViewItem? FindNavItemRecursive(IList<object> items, string tag)
    {
        foreach (var obj in items)
        {
            if (obj is NavigationViewItem item)
            {
                if (item.Tag is string t && t == tag) return item;
                var child = FindNavItemRecursive(item.MenuItems, tag);
                if (child is not null) return child;
            }
        }
        return null;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection) return;

        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            switch (tag)
            {
                case "pythonenv":
                    NavFrame.Navigate(typeof(PythonEnvPage));
                    break;
                case "nodeenv":
                    NavFrame.Navigate(typeof(NodeEnvPage));
                    break;
                case "phpenv":
                    NavFrame.Navigate(typeof(PhpEnvPage));
                    break;
                case "goenv":
                    NavFrame.Navigate(typeof(GoEnvPage));
                    break;
                case "rustenv":
                    NavFrame.Navigate(typeof(RustEnvPage));
                    break;
                case "javaenv":
                    NavFrame.Navigate(typeof(JavaEnvPage));
                    break;
                case "git":
                    NavFrame.Navigate(typeof(GitPage));
                    break;
                case "tools":
                    NavFrame.Navigate(typeof(ToolsPage));
                    break;
                case "servers":
                    NavFrame.Navigate(typeof(ServersPage));
                    break;
                case "certs":
                    NavFrame.Navigate(typeof(CertPage));
                    break;
                case "alidns":
                    NavFrame.Navigate(typeof(AlidnsPage));
                    break;
                case "proxy":
                    NavFrame.Navigate(typeof(ProxyPage));
                    break;
                case "aimonitor":
                    NavFrame.Navigate(typeof(AIMonitorPage));
                    break;
                case "projects":
                    NavFrame.Navigate(typeof(ProjectManagementPage));
                    break;
                case "home":
                    NavFrame.Navigate(typeof(HomePage));
                    break;
                case "systeminfo":
                    NavFrame.Navigate(typeof(SystemInfoPage));
                    break;
                case "about":
                    NavFrame.Navigate(typeof(AboutPage));
                    break;
            }
        }
    }

    private void OpenTerminal_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is NavigationViewItem item && item.Tag is string tag)
        {
            var exe = tag == "opencmd" ? "cmd.exe" : "powershell.exe";
            try
            {
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });
            }
            catch { }
        }
    }

    private static class NativeMethods
    {
        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public const uint HWND_TOPMOST = 0xFFFFFFFF;
        public const uint HWND_NOTOPMOST = 0xFFFFFFFE;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    }
}
