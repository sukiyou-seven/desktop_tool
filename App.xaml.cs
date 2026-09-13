using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.IO;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace DesktopTool;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// 主窗口引用，供文件选择器等功能获取窗口句柄。
    /// </summary>
    public static Window? MainWindow { get; private set; }
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
        // 全局兜底：把未处理异常写入日志，便于排查崩溃原因
        UnhandledException += (_, e) =>
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeraphineAITool");
                Directory.CreateDirectory(dir);
                var msg = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + e.Message + Environment.NewLine + e.Exception + Environment.NewLine + Environment.NewLine;
                File.AppendAllText(Path.Combine(dir, "crash.log"), msg);
            }
            catch { }
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 从本机凭据库恢复服务器地址与登录态（免登录）；已登录则后台从服务器拉取配置到内存
        ApiHelper.LoadLoginState();
        if (ApiHelper.IsLoggedIn)
        {
            _ = ConfigSync.SyncFromServerAsync();
        }

        _window = new MainWindow();
        MainWindow = _window;
        _window.Activate();
    }
}
