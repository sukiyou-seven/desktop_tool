using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopTool;

/// <summary>
/// Windows 系统代理（WinINET）设置：启用/恢复、保存原值。
/// 通过注册表 HKCU\...\Internet Settings + InternetSetOption 通知刷新。
/// </summary>
public static class SystemProxy
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const uint InternetOptionSettingsChanged = 39;
    private const uint InternetOptionRefresh = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, uint dwOption, IntPtr lpBuffer, uint dwBufferLength);

    /// <summary>读取当前系统代理状态（用于之后恢复）。</summary>
    public static ProxyState Snapshot()
    {
        var state = new ProxyState();
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, false);
        if (key is null) return state;
        state.ProxyEnable = Convert.ToInt32(key.GetValue("ProxyEnable", 0));
        state.ProxyServer = key.GetValue("ProxyServer") as string ?? "";
        state.AutoConfigURL = key.GetValue("AutoConfigURL") as string ?? "";
        return state;
    }

    /// <summary>启用系统代理并指向指定服务器（如 127.0.0.1:1080）。</summary>
    public static void Enable(string server)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, true) ?? Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", server, RegistryValueKind.String);
        Refresh();
    }

    /// <summary>恢复为保存前的系统代理状态。</summary>
    public static void Restore(ProxyState saved)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, true) ?? Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("ProxyEnable", saved.ProxyEnable, RegistryValueKind.DWord);
        if (saved.ProxyServer.Length > 0)
            key.SetValue("ProxyServer", saved.ProxyServer, RegistryValueKind.String);
        else
            key.DeleteValue("ProxyServer", false);
        if (saved.AutoConfigURL.Length > 0)
            key.SetValue("AutoConfigURL", saved.AutoConfigURL, RegistryValueKind.String);
        else
            key.DeleteValue("AutoConfigURL", false);
        Refresh();
    }

    private static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }
}

/// <summary>系统代理状态快照（含 ProxyEnable / ProxyServer / AutoConfigURL 原值）。</summary>
public class ProxyState
{
    public int ProxyEnable { get; set; }
    public string ProxyServer { get; set; } = "";
    public string AutoConfigURL { get; set; } = "";
}
