using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTool;

/// <summary>
/// 轻量 DNS TXT 记录查询（UDP 直连，协议字段按 DNS 规范使用大端序），
/// 用于确认 _acme-challenge 记录是否已生效。不依赖系统命令。
/// 优先查询本机配置的 DNS 服务器（最贴近实际解析结果），再回退公共 DNS。
/// </summary>
public static class DnsTxtResolver
{
    private static readonly string[] PublicServers = { "8.8.8.8", "1.1.1.1" };

    /// <summary>查询域名的 TXT 记录：依次尝试系统 DNS、公共 DNS，总耗时约 20 秒。
    /// 系统 DNS 返回空（可能是负缓存）时会继续回退公共 DNS，避免本机缓存未刷新导致误判。</summary>
    public static async Task<List<string>> QueryTxtAsync(string domain)
    {
        var servers = new List<string>();
        foreach (var s in GetSystemDnsServers())
            if (!servers.Contains(s)) servers.Add(s);
        foreach (var s in PublicServers)
            if (!servers.Contains(s)) servers.Add(s);

        Exception? last = null;
        List<string>? lastEmpty = null;
        foreach (var server in servers)
        {
            try
            {
                var txts = await QueryTxtAsync(server, domain);
                if (txts.Count > 0) return txts;    // 有记录：立即返回
                lastEmpty ??= txts;                 // 无记录：记下第一次空结果（可能是负缓存），继续试下一个
            }
            catch (Exception ex) { last = ex; }
        }
        if (lastEmpty != null) return lastEmpty;
        throw last ?? new IOException("DNS 查询失败");
    }

    /// <summary>读取本机网络接口配置的 DNS 服务器（IPv4，去重）。</summary>
    private static List<string> GetSystemDnsServers()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var addr in ni.GetIPProperties().DnsAddresses)
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var s = addr.ToString();
                        if (!list.Contains(s)) list.Add(s);
                    }
                }
            }
        }
        catch { }
        return list;
    }

    public static async Task<List<string>> QueryTxtAsync(string server, string domain)
    {
        var id = (ushort)Random.Shared.Next(1, 65536);
        var query = BuildQuery(id, domain);

        using var udp = new UdpClient();
        var endpoint = new IPEndPoint(IPAddress.Parse(server), 53);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await udp.SendAsync(query, query.Length, endpoint);
        var recv = await udp.ReceiveAsync(cts.Token);
        return ParseTxtResponse(recv.Buffer, id);
    }

    /// <summary>构造 DNS 查询包（全部大端序）。</summary>
    private static byte[] BuildQuery(ushort id, string domain)
    {
        using var ms = new MemoryStream();
        WriteU16(ms, id);          // ID
        ms.WriteByte(0x01);        // flags: RD
        ms.WriteByte(0x00);
        WriteU16(ms, 1);           // QDCOUNT
        WriteU16(ms, 0);           // ANCOUNT
        WriteU16(ms, 0);           // NSCOUNT
        WriteU16(ms, 0);           // ARCOUNT
        WriteName(ms, domain);
        WriteU16(ms, 16);          // QTYPE: TXT
        WriteU16(ms, 1);           // QCLASS: IN
        return ms.ToArray();
    }

    private static void WriteU16(MemoryStream ms, ushort value)
    {
        ms.WriteByte((byte)(value >> 8));
        ms.WriteByte((byte)(value & 0xFF));
    }

    private static void WriteName(MemoryStream ms, string name)
    {
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);
    }

    private static List<string> ParseTxtResponse(byte[] buf, ushort expectedId)
    {
        if (buf.Length < 12) throw new IOException("DNS 响应过短");
        var id = (ushort)((buf[0] << 8) | buf[1]);
        if (id != expectedId) throw new IOException("DNS 响应 ID 不匹配");
        var ancount = (buf[6] << 8) | buf[7];
        var offset = 12;
        ReadName(buf, ref offset); // 跳过 question name
        offset += 4;               // qtype + qclass

        var result = new List<string>();
        for (int i = 0; i < ancount && offset + 11 <= buf.Length; i++)
        {
            ReadName(buf, ref offset); // answer name（可能为压缩指针）
            var type = (buf[offset] << 8) | buf[offset + 1];
            offset += 8;              // type + class + ttl
            var rdlen = (buf[offset] << 8) | buf[offset + 1];
            offset += 2;
            var end = Math.Min(buf.Length, offset + rdlen);

            if (type == 16) // TXT
            {
                while (offset < end)
                {
                    var len = buf[offset++];
                    if (len == 0 || offset + len > end) break;
                    result.Add(Encoding.UTF8.GetString(buf, offset, len));
                    offset += len;
                }
            }
            else
            {
                offset = end;
            }
        }
        return result;
    }

    private static string ReadName(byte[] buf, ref int offset)
    {
        var labels = new List<string>();
        var jumped = false;
        var p = offset;
        var original = offset;
        while (p < buf.Length)
        {
            var len = buf[p];
            if ((len & 0xC0) == 0xC0)
            {
                if (p + 1 >= buf.Length) break;
                var ptr = ((len & 0x3F) << 8) | buf[p + 1];
                if (!jumped) original = p + 2;
                p = ptr;
                jumped = true;
                continue;
            }
            if (len == 0) { p++; break; }
            if (p + 1 + len > buf.Length) break;
            labels.Add(Encoding.ASCII.GetString(buf, p + 1, len));
            p += 1 + len;
        }
        offset = jumped ? original : p;
        return string.Join(".", labels);
    }
}
