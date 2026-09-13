using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace DesktopTool;

/// <summary>
/// 全局热键管理器：HOME 唤起、END 隐藏。
/// 实现：后台线程创建一个隐藏的 Win32 消息窗口，通过 RegisterHotKey 注册全局热键，
/// 收到 WM_HOTKEY 后用 DispatcherQueue 切回 UI 线程执行回调。
///
/// 如需调整按键/修饰键，修改下面的 VK_* 与 MOD_* 常量即可。
/// 注意：无修饰键的 HOME/END 会在所有前台应用中生效（例如编辑文档时按 END 会隐藏工具），
/// 若感觉冲突，可给 MOD_NOREPEAT 增加 MOD_CONTROL / MOD_ALT 等组合。
/// </summary>
public static class HotkeyManager
{
    // ---- 热键 ID（wParam） ----
    public const int HotKeyShow = 1;
    public const int HotKeyHide = 2;

    // ---- 按键与修饰键 ----
    private const uint VK_HOME = 0x24; // Home
    private const uint VK_END = 0x23;  // End
    private const uint MOD_NOREPEAT = 0x4000; // 仅按下瞬间触发一次，避免长按连发

    private const int WM_HOTKEY = 0x0312;
    private const int WM_CLOSE = 0x0010;
    private const string ClassName = "SeraphineHotkeyMessageWindow";

    // 保持 WndProc 委托引用，防止被 GC 回收导致窗口回调失效
    private static readonly WndProcDelegate WndProc = HotkeyWndProc;

    private static IntPtr _hwnd;
    private static DispatcherQueue? _dispatcher;
    private static Action<int>? _callback;

    /// <summary>是否已启动。</summary>
    public static bool IsStarted { get; private set; }

    /// <summary>启动热键监听（在 UI 线程调用）。</summary>
    public static void Start(DispatcherQueue dispatcher, Action<int> hotkeyCallback)
    {
        if (IsStarted) return;
        _dispatcher = dispatcher;
        _callback = hotkeyCallback;

        var thread = new Thread(WindowThread)
        {
            IsBackground = true,
            Name = "HotkeyMessageWindow",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        IsStarted = true;
    }

    /// <summary>注销热键并关闭消息窗口（应用退出时调用）。</summary>
    public static void Stop()
    {
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotKeyShow);
            UnregisterHotKey(_hwnd, HotKeyHide);
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _hwnd = IntPtr.Zero;
        }
        IsStarted = false;
    }

    private static void WindowThread()
    {
        var hInstance = GetModuleHandle(null);
        var wc = new WNDCLASS
        {
            lpfnWndProc = WndProc,
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        if (RegisterClass(ref wc) == 0)
            return;

        _hwnd = CreateWindowEx(
            0, ClassName, "SeraphineHotkey", 0,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            return;

        // 注册失败也不影响其他热键
        RegisterHotKey(_hwnd, HotKeyShow, MOD_NOREPEAT, VK_HOME);
        RegisterHotKey(_hwnd, HotKeyHide, MOD_NOREPEAT, VK_END);

        // 消息循环：收到 WM_HOTKEY 后由 WndProc 处理
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static IntPtr HotkeyWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            _dispatcher?.TryEnqueue(() => _callback?.Invoke(id));
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ---- Win32 定义 ----

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
