using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTool;

/// <summary>
/// 本地 HTTP CONNECT 代理服务器。
/// 监听本机端口接收系统代理转发来的请求，把每个连接通过远端 SOCKS5 代理
/// （可选 TLS 加密，对接服务器端 gost 的 socks5+tls）转发出去。
/// 用于「一键代理」功能：配合系统代理设置，浏览器等应用的流量即可走远端服务器出口。
/// </summary>
public class LocalProxyServer : IDisposable
{
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly List<TcpClient> _clients = new();

    public string RemoteHost { get; }
    public int RemotePort { get; }
    public string? Username { get; }
    public string? Password { get; }
    public bool UseTls { get; }
    public bool SkipCertVerify { get; }
    public int LocalPort { get; }
    public bool IsRunning { get; private set; }

    /// <summary>日志回调（页面显示用，可能从后台线程触发）。</summary>
    public event Action<string>? Log;

    public LocalProxyServer(string remoteHost, int remotePort, string? username, string? password,
        bool useTls, bool skipCertVerify, int localPort)
    {
        RemoteHost = remoteHost;
        RemotePort = remotePort;
        Username = string.IsNullOrEmpty(username) ? null : username;
        Password = string.IsNullOrEmpty(password) ? null : password;
        UseTls = useTls;
        SkipCertVerify = skipCertVerify;
        LocalPort = localPort;
    }

    public void Start()
    {
        if (IsRunning) return;
        _listener = new TcpListener(IPAddress.Loopback, LocalPort);
        _listener.Start();
        IsRunning = true;
        _ = AcceptLoopAsync();
        Log?.Invoke($"本地代理已启动：127.0.0.1:{LocalPort} → {RemoteHost}:{RemotePort}" + (UseTls ? "（TLS 加密）" : "（明文）"));
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        lock (_lock)
        {
            foreach (var c in _clients) { try { c.Dispose(); } catch { } }
            _clients.Clear();
        }
        IsRunning = false;
        Log?.Invoke("本地代理已停止");
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener!.AcceptTcpClientAsync(_cts.Token); }
                catch { break; }
                lock (_lock) _clients.Add(client);
                _ = HandleClientAsync(client);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("代理监听异常：" + ex.Message);
        }
    }

    private async Task HandleClientAsync(TcpClient local)
    {
        try
        {
            using (local)
            {
                local.NoDelay = true;
                var stream = local.GetStream();

                // 读取 HTTP 请求头（含第一行）
                var header = await ReadHttpHeaderAsync(stream);
                if (header is null) return;
                var (firstLine, raw) = header.Value;

                if (!TryParseTarget(firstLine, out var host, out var port))
                {
                    await WriteHttpErrorAsync(stream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    return;
                }

                // 连接远端 SOCKS5（可选 TLS）
                using var remote = await ConnectRemoteAsync();
                await Socks5HandshakeAsync(remote.Stream, host, port);

                if (firstLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));
                }
                else
                {
                    // 普通 HTTP 请求：把原始请求头（补齐 \r\n\r\n 结尾）转发给远端，其余字节由 Relay 继续转发
                    await remote.Stream.WriteAsync(Encoding.ASCII.GetBytes(raw + "\r\n\r\n"));
                }

                await RelayAsync(stream, remote.Stream);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("连接处理失败：" + ex.Message);
        }
        finally
        {
            lock (_lock) _clients.Remove(local);
        }
    }

    /// <summary>建立到远端服务器的连接（可选 TLS 加密）。</summary>
    private async Task<RemoteConnection> ConnectRemoteAsync()
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(RemoteHost, RemotePort);
        tcp.NoDelay = true;
        var ns = tcp.GetStream();
        if (UseTls)
        {
            var ssl = new SslStream(ns, false,
                (_, _, _, _) => SkipCertVerify, null);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = RemoteHost });
            return new RemoteConnection(ssl, tcp);
        }
        return new RemoteConnection(ns, tcp);
    }

    private sealed class RemoteConnection : IDisposable
    {
        public Stream Stream { get; }
        private readonly TcpClient _client;

        public RemoteConnection(Stream stream, TcpClient client)
        {
            Stream = stream;
            _client = client;
        }

        public void Dispose()
        {
            try { Stream.Dispose(); } catch { }
            try { _client.Dispose(); } catch { }
        }
    }

    /// <summary>SOCKS5 握手（方法协商 + 用户密码认证（可选）+ CONNECT 目标）。</summary>
    private async Task Socks5HandshakeAsync(Stream s, string host, int port)
    {
        // 1) 方法协商
        byte[] greeting = Username is not null
            ? new byte[] { 5, 1, 2 }           // 支持用户名/密码认证
            : new byte[] { 5, 1, 0 };          // 无认证
        await s.WriteAsync(greeting.AsMemory());
        var resp = new byte[2];
        await ReadExactAsync(s, resp);
        if (resp[0] != 5) throw new IOException("SOCKS5 协议版本错误");
        if (resp[1] == 0xff) throw new IOException("服务器不支持可用认证方式");

        // 2) 用户名/密码认证（RFC 1929）
        if (Username is not null)
        {
            if (resp[1] != 2) throw new IOException("服务器未要求用户名密码认证");
            var u = Encoding.UTF8.GetBytes(Username);
            var p = Encoding.UTF8.GetBytes(Password ?? "");
            var auth = new byte[1 + 1 + u.Length + 1 + p.Length];
            auth[0] = 1;
            auth[1] = (byte)u.Length;
            Buffer.BlockCopy(u, 0, auth, 2, u.Length);
            auth[2 + u.Length] = (byte)p.Length;
            Buffer.BlockCopy(p, 0, auth, 3 + u.Length, p.Length);
            await s.WriteAsync(auth.AsMemory());
            var authResp = new byte[2];
            await ReadExactAsync(s, authResp);
            if (authResp[0] != 1 || authResp[1] != 0) throw new IOException("SOCKS5 认证失败（用户名/密码错误）");
        }

        // 3) CONNECT
        var hostBytes = Encoding.UTF8.GetBytes(host);
        var buf = new byte[4 + 1 + hostBytes.Length + 2];
        buf[0] = 5; buf[1] = 1; buf[2] = 0; buf[3] = 3;
        buf[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, buf, 5, hostBytes.Length);
        buf[5 + hostBytes.Length] = (byte)(port >> 8);
        buf[6 + hostBytes.Length] = (byte)(port & 0xFF);
        await s.WriteAsync(buf.AsMemory());

        var rep = new byte[4];
        await ReadExactAsync(s, rep);
        if (rep[0] != 5) throw new IOException("SOCKS5 响应版本错误");
        if (rep[1] != 0)
            throw new IOException("SOCKS5 连接目标失败，错误码 " + rep[1] + "（0=成功 1=常规失败 2=规则集 3=网络不可达 4=主机不可达 5=拒绝 6=TTL 过期 7=命令不支持 8=地址类型不支持）");
        // 跳过 BND.ADDR / BND.PORT
        await SkipBndAddrAsync(s, rep);
    }

    private static async Task SkipBndAddrAsync(Stream s, byte[] rep)
    {
        switch (rep[3])
        {
            case 1: // IPv4
                await ReadExactAsync(s, new byte[4 + 2]);
                break;
            case 3: // 域名
                var lenB = new byte[1];
                await ReadExactAsync(s, lenB);
                await ReadExactAsync(s, new byte[lenB[0] + 2]);
                break;
            case 4: // IPv6
                await ReadExactAsync(s, new byte[16 + 2]);
                break;
            default:
                throw new IOException("SOCKS5 不支持的地址类型 " + rep[3]);
        }
    }

    /// <summary>双向转发，任一端关闭即结束。</summary>
    private static async Task RelayAsync(Stream a, Stream b)
    {
        var t1 = a.CopyToAsync(b);
        var t2 = b.CopyToAsync(a);
        await Task.WhenAny(t1, t2);
        try { a.Dispose(); } catch { }
        try { b.Dispose(); } catch { }
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

    private static async Task<(string FirstLine, string Raw)?> ReadHttpHeaderAsync(Stream s)
    {
        var ms = new MemoryStream();
        var buf = new byte[4096];
        while (ms.Length < 64 * 1024)
        {
            int n = await s.ReadAsync(buf.AsMemory());
            if (n == 0) return null;
            ms.Write(buf, 0, n);
            var data = ms.ToArray();
            int idx = IndexOfHeaderEnd(data);
            if (idx >= 0)
            {
                var raw = Encoding.ASCII.GetString(data, 0, idx);
                var firstLine = raw.Split("\r\n")[0];
                return (firstLine, raw);
            }
        }
        return null;
    }

    private static int IndexOfHeaderEnd(byte[] data)
    {
        for (int i = 0; i <= data.Length - 4; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static bool TryParseTarget(string firstLine, out string host, out int port)
    {
        host = "";
        port = 0;
        var parts = firstLine.Split(' ');
        if (parts.Length < 2) return false;
        var method = parts[0].ToUpperInvariant();
        var target = parts[1];
        if (method == "CONNECT")
        {
            var idx = target.LastIndexOf(':');
            if (idx <= 0) return false;
            host = target.Substring(0, idx);
            return int.TryParse(target.Substring(idx + 1), out port) && port > 0;
        }
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            host = uri.Host;
            port = uri.Port;
            return port > 0;
        }
        return false;
    }

    private static async Task WriteHttpErrorAsync(Stream s, string text)
    {
        try
        {
            var b = Encoding.ASCII.GetBytes(text);
            await s.WriteAsync(b.AsMemory());
            await s.FlushAsync();
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
