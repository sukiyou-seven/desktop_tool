using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;

namespace DesktopTool.Pages;

/// <summary>
/// 一行信息（标签 + 值），用于 ItemsControl 列表展示。
/// </summary>
public class InfoItem
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";

    /// <summary>是否在行尾显示一个操作按钮（用于工具链板块的下载安装入口）。</summary>
    public bool HasAction { get; set; }
    public string ActionText { get; set; } = "下载安装";
    public string ActionTag { get; set; } = "";
    public Visibility ActionVisibility => HasAction ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// 一块磁盘/分区的信息。
/// </summary>
public class DiskInfo
{
    public string Name { get; set; } = "";
    public string DriveType { get; set; } = "";
    public string Summary { get; set; } = "";
    public double UsedPercent { get; set; }
}

/// <summary>
/// 系统信息页面：展示操作系统、硬件与开发环境概览。
/// </summary>
public sealed partial class SystemInfoPage : Page
{
    public ObservableCollection<InfoItem> OsItems { get; } = new();
    public ObservableCollection<InfoItem> CpuItems { get; } = new();
    public ObservableCollection<InfoItem> MemoryItems { get; } = new();
    public ObservableCollection<DiskInfo> Disks { get; } = new();
    public ObservableCollection<InfoItem> ActivationInfoItems { get; } = new();
    public ObservableCollection<InfoItem> DevItems { get; } = new();

    public SystemInfoPage()
    {
        InitializeComponent();
        Loaded += SystemInfoPage_Loaded;
    }

    private void SystemInfoPage_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSystemInfo();
    }

    // ---------------------------------------------------------------------
    // 数据收集
    // ---------------------------------------------------------------------

    private void LoadSystemInfo()
    {
        LoadOsInfo();
        LoadCpuInfo();
        LoadMemoryInfo();
        LoadDiskInfo();
        _ = LoadActivationInfoAsync();
        LoadDevInfo();
    }

    private void LoadOsInfo()
    {
        var productName = ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", "Windows");
        var displayVersion = ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "");
        var build = ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "");
        var ubr = ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", "");

        var osTitle = productName;
        var osVersionLine = string.IsNullOrEmpty(displayVersion)
            ? $"内部版本 {build}"
            : $"版本 {displayVersion} (内部版本 {build}.{ubr})";

        OsOverviewText.Text = osTitle;
        OsVersionOverviewText.Text = osVersionLine;

        // 开机时间：从系统运行时长反推
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var bootTime = DateTime.Now - uptime;

        OsItems.Add(new InfoItem { Label = "操作系统", Value = productName });
        OsItems.Add(new InfoItem { Label = "版本", Value = osVersionLine });
        OsItems.Add(new InfoItem { Label = "系统目录", Value = Environment.SystemDirectory });
        OsItems.Add(new InfoItem { Label = "计算机名", Value = Environment.MachineName });
        OsItems.Add(new InfoItem { Label = "当前用户", Value = Environment.UserName });
        OsItems.Add(new InfoItem { Label = "系统架构", Value = RuntimeInformation.OSArchitecture.ToString() });
        OsItems.Add(new InfoItem { Label = "进程架构", Value = RuntimeInformation.ProcessArchitecture.ToString() });
        OsItems.Add(new InfoItem { Label = "开机时间", Value = bootTime.ToString("yyyy-MM-dd HH:mm:ss") });
        OsItems.Add(new InfoItem { Label = "已运行时长", Value = FormatUptime(uptime) });
        OsItems.Add(new InfoItem { Label = "系统语言", Value = System.Globalization.CultureInfo.InstalledUICulture.DisplayName });
        OsItems.Add(new InfoItem { Label = "时区", Value = TimeZoneInfo.Local.DisplayName });
    }

    private void LoadCpuInfo()
    {
        // CPU 名称来自注册表 HARDWARE 键（只读）
        var cpuName = ReadRegistryString(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "未知处理器");

        var logicalCores = Environment.ProcessorCount;
        var physicalCores = GetPhysicalProcessorCount();
        var physicalText = physicalCores > 0 ? physicalCores.ToString() : "未知";

        CpuOverviewText.Text = Shorten(cpuName, 32);
        CpuCoreOverviewText.Text = $"{logicalCores} 逻辑处理器";

        CpuItems.Add(new InfoItem { Label = "处理器型号", Value = cpuName });
        CpuItems.Add(new InfoItem { Label = "物理核心数", Value = physicalText });
        CpuItems.Add(new InfoItem { Label = "逻辑处理器数", Value = logicalCores.ToString() });
        CpuItems.Add(new InfoItem { Label = "CPU 架构", Value = RuntimeInformation.ProcessArchitecture.ToString() });
    }

    private void LoadMemoryInfo()
    {
        var mem = GetMemoryStatus();
        if (mem is null)
        {
            MemoryOverviewText.Text = "无法读取";
            MemoryFreeOverviewText.Text = "";
            MemoryItems.Add(new InfoItem { Label = "内存状态", Value = "读取失败" });
            return;
        }

        ulong total = mem.Value.ullTotalPhys;
        ulong avail = mem.Value.ullAvailPhys;
        ulong used = total - avail;
        double usedPercent = total == 0 ? 0 : (used * 100.0) / total;

        MemoryOverviewText.Text = FormatBytes(total);
        MemoryFreeOverviewText.Text = $"可用 {FormatBytes(avail)}";

        MemoryItems.Add(new InfoItem { Label = "总物理内存", Value = FormatBytes(total) });
        MemoryItems.Add(new InfoItem { Label = "可用内存", Value = FormatBytes(avail) });
        MemoryItems.Add(new InfoItem { Label = "已使用", Value = $"{FormatBytes(used)} ({usedPercent:0.0}%)" });
        MemoryItems.Add(new InfoItem { Label = "内存负载", Value = $"{mem.Value.dwMemoryLoad}%" });
        MemoryItems.Add(new InfoItem { Label = "虚拟内存总量", Value = FormatBytes(mem.Value.ullTotalVirtual) });
        MemoryItems.Add(new InfoItem { Label = "虚拟内存可用", Value = FormatBytes(mem.Value.ullAvailVirtual) });
    }

    private void LoadDiskInfo()
    {
        ulong totalAll = 0, freeAll = 0;
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                {
                    Disks.Add(new DiskInfo
                    {
                        Name = drive.Name,
                        DriveType = "未就绪",
                        Summary = "无法访问（未就绪或需要权限）",
                        UsedPercent = 0,
                    });
                    continue;
                }

                ulong total = (ulong)drive.TotalSize;
                ulong free = (ulong)drive.AvailableFreeSpace;
                ulong used = total > free ? total - free : 0;
                double usedPercent = total == 0 ? 0 : (used * 100.0) / total;

                if (total > 0)
                {
                    totalAll += total;
                    freeAll += free;
                }

                var typeText = drive.DriveType switch
                {
                    DriveType.Fixed => "本地磁盘",
                    DriveType.Removable => "可移动磁盘",
                    DriveType.Network => "网络驱动器",
                    DriveType.CDRom => "光盘",
                    DriveType.Ram => "内存盘",
                    _ => drive.DriveType.ToString(),
                };

                Disks.Add(new DiskInfo
                {
                    Name = drive.Name,
                    DriveType = typeText,
                    Summary = $"{FormatBytes(used)} / {FormatBytes(total)}  ·  可用 {FormatBytes(free)}",
                    UsedPercent = usedPercent,
                });
            }
        }
        catch (Exception ex)
        {
            Disks.Add(new DiskInfo { Name = "—", DriveType = "错误", Summary = ex.Message, UsedPercent = 0 });
        }

        DiskOverviewText.Text = $"{Disks.Count(d => d.UsedPercent > 0 || d.Summary != "无法访问（未就绪或需要权限）")} 个分区";
        DiskFreeOverviewText.Text = $"可用 {FormatBytes(freeAll)} / {FormatBytes(totalAll)}";
    }

    private async Task LoadActivationInfoAsync()
    {
        ActivationSummaryText.Text = "读取中…";
        ActivationInfoItems.Clear();
        try
        {
            var info = await Task.Run(WindowsActivation.Query);
            ActivationSummaryText.Text = info.IsActivated ? "✔ 已激活" : info.LicenseStatusText;
            ActivationInfoItems.Add(new InfoItem { Label = "版本", Value = info.ProductName });
            ActivationInfoItems.Add(new InfoItem { Label = "激活状态", Value = info.LicenseStatusText });
            ActivationInfoItems.Add(new InfoItem { Label = "授权类型", Value = info.ProductKeyChannel });
            ActivationInfoItems.Add(new InfoItem { Label = "产品密钥（尾号）", Value = info.PartialProductKey });
            ActivationInfoItems.Add(new InfoItem { Label = "到期 / 宽限期", Value = info.ExpirationText });
        }
        catch (Exception ex)
        {
            ActivationSummaryText.Text = "读取失败";
            ActivationInfoItems.Add(new InfoItem { Label = "错误", Value = ex.Message });
        }
    }

    private void RefreshActivationBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadActivationInfoAsync();
    }

    private void InstallKeyBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = RunActivationOpAsync(() => WindowsActivation.InstallProductKey(ProductKeyBox.Text));
    }

    private void ActivateBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = RunActivationOpAsync(WindowsActivation.ActivateOnline);
    }

    private void SetKmsServerBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = RunActivationOpAsync(() => WindowsActivation.SetKmsServer(KmsServerBox.Text));
    }

    private void ShowLicenseBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = RunActivationOpAsync(WindowsActivation.ShowLicenseInfo);
    }

    private async Task RunActivationOpAsync(Func<string> op)
    {
        if (!WindowsActivation.IsAdministrator)
        {
            ActivationOpResult.Text = "此操作需要管理员权限：请以管理员身份运行本程序后再试。";
            return;
        }

        ActivationOpResult.Text = "正在执行…";
        try
        {
            var result = await Task.Run(op);
            ActivationOpResult.Text = result;
        }
        catch (Exception ex)
        {
            ActivationOpResult.Text = "执行失败：" + ex.Message;
        }
    }

    private void LoadDevInfo()
    {
        // 当前进程运行的 .NET 运行时
        DevItems.Add(new InfoItem { Label = ".NET 运行时", Value = RuntimeInformation.FrameworkDescription });

        // 已安装的 .NET SDK / 运行时（目录扫描，打包应用可能受限）
        var sdkVersions = TryListDirectories(@"C:\Program Files\dotnet\sdk");
        DevItems.Add(new InfoItem
        {
            Label = ".NET SDK 版本",
            Value = sdkVersions is null
                ? "无法读取（打包应用权限受限，请以管理员运行）"
                : sdkVersions.Count == 0
                    ? "未检测到 SDK"
                    : string.Join("  ·  ", sdkVersions),
        });

        var runtimeVersions = new List<string>();
        try
        {
            var sharedRoot = @"C:\Program Files\dotnet\shared";
            foreach (var dir in Directory.GetDirectories(sharedRoot))
            {
                var framework = Path.GetFileName(dir);
                foreach (var ver in Directory.GetDirectories(dir).Select(Path.GetFileName).OrderByDescending(v => v))
                {
                    runtimeVersions.Add($"{framework} {ver}");
                }
            }
            DevItems.Add(new InfoItem
            {
                Label = ".NET 运行时列表",
                Value = runtimeVersions.Count == 0 ? "未检测到运行时" : string.Join("\n", runtimeVersions),
            });
        }
        catch
        {
            DevItems.Add(new InfoItem { Label = ".NET 运行时列表", Value = "无法读取（打包应用权限受限）" });
        }
    }

    // ---------------------------------------------------------------------
    // 辅助函数
    // ---------------------------------------------------------------------

    private static string ReadRegistryString(string subKey, string name, string fallback)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            var value = key?.GetValue(name)?.ToString();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    private static int GetPhysicalProcessorCount()
    {
        // 通过注册表 CentralProcessor 下的处理器条目数估算物理封装数量不可靠，
        // 这里直接尝试用环境变量/系统信息；失败返回 0。
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor");
            return key?.GetSubKeyNames().Length ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatUptime(TimeSpan t)
    {
        if (t.TotalDays >= 1)
            return $"{(int)t.TotalDays} 天 {t.Hours} 小时 {t.Minutes} 分钟";
        if (t.TotalHours >= 1)
            return $"{t.Hours} 小时 {t.Minutes} 分钟";
        return $"{t.Minutes} 分钟";
    }

    private static string Shorten(string s, int max)
    {
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    // ---------------------------------------------------------------------
    // 原生 API
    // ---------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    private static MemoryStatusEx? GetMemoryStatus()
    {
        try
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? status : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<string>? TryListDirectories(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return new List<string>();
            return Directory.GetDirectories(path)
                .Select(p => new DirectoryInfo(p).Name)
                .OrderByDescending(v => v)
                .ToList();
        }
        catch
        {
            return null;
        }
    }
}
