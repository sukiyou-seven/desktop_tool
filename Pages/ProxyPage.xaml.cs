using System;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopTool.Pages;

/// <summary>
/// 网络代理页：一键把本机网络通过远端 SOCKS5 服务器（gost）转发。
/// 本地起 HTTP CONNECT 代理 + 设置系统代理，支持 TLS 加密。
/// </summary>
public sealed partial class ProxyPage : Page
{
    private LocalProxyServer? _server;
    private ProxyState? _savedProxy;
    private bool _busy;

    public ProxyPage()
    {
        InitializeComponent();
        Loaded += ProxyPage_Loaded;
        Unloaded += ProxyPage_Unloaded;
    }

    private void ProxyPage_Loaded(object sender, RoutedEventArgs e)
    {
        ServerBox.Text = ConfigSync.Get<string>("proxy_server", "") ?? "";
        PortBox.Text = ConfigSync.Get<string>("proxy_port", "1080") ?? "1080";
        UserBox.Text = ConfigSync.Get<string>("proxy_user", "") ?? "";
        PassBox.Password = ConfigSync.Get<string>("proxy_pass", "") ?? "";
        LocalPortBox.Text = ConfigSync.Get<string>("proxy_local_port", "1080") ?? "1080";
        TlsCheck.IsChecked = ConfigSync.Get<string>("proxy_tls", "1") != "0";
        SkipCertCheck.IsChecked = ConfigSync.Get<string>("proxy_skipverify", "1") != "0";
        UpdateUi();
    }

    private void ProxyPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // 页面切走时保持代理运行（由用户手动关闭），这里不自动关闭。
    }

    // ---------------- 一键开启 / 关闭 ----------------

    private async void ToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_server is { IsRunning: true })
        {
            StopProxy();
            return;
        }
        await StartProxyAsync();
    }

    private async Task StartProxyAsync()
    {
        var server = ServerBox.Text.Trim();
        var portText = PortBox.Text.Trim();
        var localPortText = LocalPortBox.Text.Trim();
        if (server.Length == 0 || !int.TryParse(portText, out var port) || port <= 0)
        {
            SetStatus("请填写正确的服务器地址和端口", isError: true);
            return;
        }
        if (!int.TryParse(localPortText, out var localPort) || localPort <= 0 || localPort > 65535)
        {
            SetStatus("本地端口不合法（1-65535）", isError: true);
            return;
        }

        // 保存配置
        ConfigSync.Set("proxy_server", server);
        ConfigSync.Set("proxy_port", portText);
        ConfigSync.Set("proxy_user", UserBox.Text.Trim());
        ConfigSync.Set("proxy_pass", PassBox.Password);
        ConfigSync.Set("proxy_local_port", localPortText);
        ConfigSync.Set("proxy_tls", (TlsCheck.IsChecked ?? true) ? "1" : "0");
        ConfigSync.Set("proxy_skipverify", (SkipCertCheck.IsChecked ?? true) ? "1" : "0");

        SetBusy(true);
        try
        {
            // 先保存系统代理原值
            _savedProxy = SystemProxy.Snapshot();
            Log($"保存当前系统代理设置：启用={_savedProxy.ProxyEnable} 服务器={(_savedProxy.ProxyServer.Length > 0 ? _savedProxy.ProxyServer : "(无)")}");

            _server = new LocalProxyServer(
                server, port,
                string.IsNullOrEmpty(UserBox.Text.Trim()) ? null : UserBox.Text.Trim(),
                PassBox.Password,
                TlsCheck.IsChecked ?? true,
                SkipCertCheck.IsChecked ?? true,
                localPort);
            _server.Log += ProxyLog;
            _server.Start();
            if (!_server.IsRunning)
                throw new InvalidOperationException("本地代理启动失败（可能端口被占用）");

            SystemProxy.Enable("127.0.0.1:" + localPort);
            Log("已设置系统代理为 127.0.0.1:" + localPort + "，浏览器等应用流量将走代理。");
            SetStatus("代理已开启", isError: false, ok: true);
        }
        catch (Exception ex)
        {
            Log("开启失败：" + ex.Message);
            SetStatus("开启失败：" + ex.Message, isError: true);
            try { _server?.Stop(); } catch { }
            if (_savedProxy is not null) SystemProxy.Restore(_savedProxy);
            _server = null;
            _savedProxy = null;
        }
        finally
        {
            SetBusy(false);
            UpdateUi();
        }
    }

    private void StopProxy()
    {
        try
        {
            _server?.Stop();
            if (_savedProxy is not null)
            {
                SystemProxy.Restore(_savedProxy);
                Log("已恢复系统代理设置。");
            }
            SetStatus("代理已关闭", isError: false, ok: true);
        }
        catch (Exception ex)
        {
            Log("关闭异常：" + ex.Message);
            SetStatus("关闭异常：" + ex.Message, isError: true);
        }
        finally
        {
            _server = null;
            _savedProxy = null;
            UpdateUi();
        }
    }

    // ---------------- IKEv2 VPN（电脑端一键连接） ----------------

    private async void VpnConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        var server = VpnServerBox.Text.Trim();
        var user = VpnUserBox.Text.Trim();
        var pass = VpnPassBox.Password;
        if (server.Length == 0 || user.Length == 0)
        {
            VpnStatusText.Text = "请填写服务器地址和用户名";
            return;
        }

        VpnStatusText.Text = "正在连接…";
        try
        {
            var result = await Task.Run(() => Ikev2Vpn.Connect(server, user, pass));
            if (Ikev2Vpn.IsConnected())
                VpnStatusText.Text = "已连接 ✓（全流量走服务器出口）";
            else
                VpnStatusText.Text = result;
        }
        catch (Exception ex)
        {
            VpnStatusText.Text = "连接失败：" + ex.Message;
        }
    }

    private void VpnDisconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = Ikev2Vpn.Disconnect();
            VpnStatusText.Text = Ikev2Vpn.IsConnected() ? "断开失败：" + result : "已断开";
        }
        catch (Exception ex)
        {
            VpnStatusText.Text = "断开失败：" + ex.Message;
        }
    }

    // ---------------- 测试连接 ----------------

    private async void TestBtn_Click(object sender, RoutedEventArgs e)
    {
        var server = ServerBox.Text.Trim();
        if (server.Length == 0 || !int.TryParse(PortBox.Text.Trim(), out var port) || port <= 0)
        {
            SetStatus("请先填写服务器地址和端口", isError: true);
            return;
        }
        SetBusy(true);
        try
        {
            Log($"测试连接 {server}:{port}" + ((TlsCheck.IsChecked ?? true) ? "（TLS）" : "（明文）") + " ...");
            var sw = Stopwatch.StartNew();
            await TestConnectionAsync(server, port,
                TlsCheck.IsChecked ?? true,
                SkipCertCheck.IsChecked ?? true,
                UserBox.Text.Trim(), PassBox.Password);
            sw.Stop();
            Log($"测试成功，耗时 {sw.ElapsedMilliseconds} ms（连接 + SOCKS5 握手通过）。");
            SetStatus("连接测试成功", isError: false, ok: true);
        }
        catch (Exception ex)
        {
            Log("测试失败：" + ex.Message);
            SetStatus("连接测试失败：" + ex.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static async Task TestConnectionAsync(string host, int port, bool useTls, bool skipVerify, string user, string pass)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port);
        tcp.NoDelay = true;
        Stream stream = tcp.GetStream();
        if (useTls)
        {
            var ssl = new SslStream(stream, false, (_, _, _, _) => skipVerify, null);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host });
            stream = ssl;
        }
        // SOCKS5 方法协商（带认证选项），不做实际 CONNECT，验证连通与认证
        byte[] greeting = string.IsNullOrEmpty(user) ? new byte[] { 5, 1, 0 } : new byte[] { 5, 1, 2 };
        await stream.WriteAsync(greeting.AsMemory());
        var resp = new byte[2];
        await ReadExactAsync(stream, resp);
        if (resp[0] != 5) throw new InvalidOperationException("服务器不是 SOCKS5 协议");
        if (resp[1] == 0xff) throw new InvalidOperationException("服务器没有可用的认证方式（请检查 gost 用户名密码）");
        if (!string.IsNullOrEmpty(user))
        {
            if (resp[1] != 2) throw new InvalidOperationException("服务器未要求用户名密码认证，请检查 gost 配置");
            var u = Encoding.UTF8.GetBytes(user);
            var p = Encoding.UTF8.GetBytes(pass ?? "");
            var auth = new byte[1 + 1 + u.Length + 1 + p.Length];
            auth[0] = 1; auth[1] = (byte)u.Length;
            Buffer.BlockCopy(u, 0, auth, 2, u.Length);
            auth[2 + u.Length] = (byte)p.Length;
            Buffer.BlockCopy(p, 0, auth, 3 + u.Length, p.Length);
            await stream.WriteAsync(auth.AsMemory());
            var authResp = new byte[2];
            await ReadExactAsync(stream, authResp);
            if (authResp[0] != 1 || authResp[1] != 0)
                throw new InvalidOperationException("认证失败（用户名/密码错误，请检查 gost 配置）");
        }
    }

    private static async Task ReadExactAsync(Stream s, byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(off, buf.Length - off));
            if (n == 0) throw new EndOfStreamException("连接被对端关闭");
            off += n;
        }
    }

    // ---------------- 界面辅助 ----------------

    private void ProxyLog(string line) => Log(line);

    private void Log(string line)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            LogText.Text += $"[{DateTime.Now:HH:mm:ss}] {line}\n";
            try { LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null); } catch { }
        });
    }

    private void SetStatus(string text, bool isError, bool ok = false)
    {
        StatusText.Text = text;
        try
        {
            StatusText.Foreground = isError
                ? (Microsoft.UI.Xaml.Media.Brush)Resources["ProxyErrorBrush"]
                : ok ? (Microsoft.UI.Xaml.Media.Brush)Resources["ProxyOkBrush"]
                     : (Microsoft.UI.Xaml.Media.Brush)Resources["ProxyInfoBrush"];
        }
        catch
        {
            StatusText.Foreground = null;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        UpdateUi();
    }

    private void UpdateUi()
    {
        var running = _server is { IsRunning: true };
        ToggleBtn.Content = running ? "关闭代理" : "一键开启代理";
        TestBtn.IsEnabled = !_busy && !running;
        ServerBox.IsEnabled = !running;
        PortBox.IsEnabled = !running;
        UserBox.IsEnabled = !running;
        PassBox.IsEnabled = !running;
        LocalPortBox.IsEnabled = !running;
        TlsCheck.IsEnabled = !running;
        SkipCertCheck.IsEnabled = !running;
    }
}
