using System;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace DesktopTool;

/// <summary>
/// 管理本工具的开机自启，自动适配两种部署形态：
///   - 免安装（unpackaged）：写入 HKCU\Software\...\Run 注册表项（当前用户，无需管理员）；
///   - MSIX 打包（packaged）：使用 StartupTask 启动任务（系统"设置→应用→启动"可见、可管理）。
/// 二者仅在应用内开关控制，卸载时均不残留系统级设置。
/// </summary>
public static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SeraphineAITool";
    private const string StartupTaskId = "SeraphineAITool";

    /// <summary>当前是否运行在 MSIX 打包环境（是否有包标识）。</summary>
    public static bool IsPackaged
    {
        get
        {
            try { return Package.Current is not null; }
            catch { return false; }
        }
    }

    /// <summary>当前是否为开机自启状态。</summary>
    public static async Task<bool> IsEnabledAsync()
    {
        if (IsPackaged)
        {
            try
            {
                var task = await StartupTask.GetAsync(StartupTaskId);
                return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }
            catch
            {
                return false;
            }
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>开启自启。返回是否成功启用（StartupTask 可能被用户/策略禁用而失败）。</summary>
    public static async Task<bool> EnableAsync()
    {
        if (IsPackaged)
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            var state = await task.RequestEnableAsync();
            return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            throw new InvalidOperationException("无法获取当前程序路径");
        key?.SetValue(ValueName, $"\"{exe}\"");
        return true;
    }

    /// <summary>关闭自启；未开启时静默忽略。</summary>
    public static async Task DisableAsync()
    {
        if (IsPackaged)
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            task.Disable();
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
