using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DesktopTool;

public class ApiHelper
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>默认服务器地址（代码常量，部署时可修改此值）。</summary>
    public const string DefaultServerUrl = "http://127.0.0.1:5000";

    private const string ServerTarget = "seraphine:server";
    private const string LoginTarget = "seraphine:login";

    // 运行期内存状态；登录态通过 Windows 凭据管理器持久化（免登录），配置本身仍为纯云端。
    private static string _serverUrl = DefaultServerUrl;
    private static string? _userId;
    private static string? _username;
    private static string? _token;
    private static string? _refreshToken;
    private static string? _avatar;

    public static string ServerUrl
    {
        get => _serverUrl;
        set => _serverUrl = string.IsNullOrWhiteSpace(value) ? DefaultServerUrl : value.Trim();
    }

    public static string? UserId { get => _userId; set => _userId = value; }
    public static string? Username { get => _username; set => _username = value; }
    public static string? Token { get => _token; set => _token = value; }
    public static string? RefreshToken { get => _refreshToken; set => _refreshToken = value; }
    public static string? Avatar { get => _avatar; set => _avatar = value; }

    public static bool IsLoggedIn => !string.IsNullOrEmpty(Token);

    // ---------------------------------------------------------------------
    // 登录态持久化（Windows 凭据管理器；token 明文不落盘）
    // ---------------------------------------------------------------------

    /// <summary>
    /// 启动时调用：从本机凭据库恢复服务器地址与登录态。
    /// 返回 true 表示已恢复登录（免登录进入）。
    /// </summary>
    public static bool LoadLoginState()
    {
        try
        {
            // 服务器地址独立凭据，可在未登录时单独保存
            if (WindowsCredentialStore.TryRead(ServerTarget, out _, out var serverJson) && !string.IsNullOrEmpty(serverJson))
            {
                try
                {
                    var srv = JsonSerializer.Deserialize<Dictionary<string, string?>>(serverJson);
                    _serverUrl = srv?.GetValueOrDefault("server_url") ?? DefaultServerUrl;
                }
                catch { }
            }

            if (!WindowsCredentialStore.TryRead(LoginTarget, out _, out var secret) || string.IsNullOrEmpty(secret))
                return false;

            var dict = JsonSerializer.Deserialize<Dictionary<string, string?>>(secret);
            if (dict is null) return false;

            _userId = dict.GetValueOrDefault("user_id");
            _username = dict.GetValueOrDefault("username");
            _token = dict.GetValueOrDefault("token");
            _refreshToken = dict.GetValueOrDefault("refresh_token");
            _avatar = LoadAvatarFile();
            return IsLoggedIn;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>登录成功后调用：把登录态写入本机凭据库，下次启动免登录。</summary>
    public static void SaveLoginState()
    {
        try
        {
            var data = new Dictionary<string, string?>
            {
                ["user_id"] = _userId,
                ["username"] = _username,
                ["token"] = _token,
                ["refresh_token"] = _refreshToken,
            };
            WindowsCredentialStore.Save(LoginTarget, _username ?? "seraphine", JsonSerializer.Serialize(data));
            SaveServerUrl();
            SaveAvatarFile();
        }
        catch { }
    }

    /// <summary>保存服务器地址（独立于登录态，未登录时也可修改）。</summary>
    public static void SaveServerUrl()
    {
        try
        {
            var data = new Dictionary<string, string?> { ["server_url"] = _serverUrl };
            WindowsCredentialStore.Save(ServerTarget, "seraphine", JsonSerializer.Serialize(data));
        }
        catch { }
    }

    /// <summary>清空登录态（内存 + 凭据库 + 本地头像缓存）。</summary>
    public static void ClearCredentials()
    {
        _userId = null;
        _username = null;
        _token = null;
        _refreshToken = null;
        _avatar = null;
        try { WindowsCredentialStore.Delete(LoginTarget); } catch { }
        try
        {
            var avatarFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeraphineAITool", "avatar.txt");
            if (File.Exists(avatarFile)) File.Delete(avatarFile);
        }
        catch { }
    }

    // 头像 base64 可能超过凭据 blob 上限（约 2.5KB），单独存本地缓存文件（非敏感数据）
    private static string? LoadAvatarFile()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeraphineAITool", "avatar.txt");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch { return null; }
    }

    private static void SaveAvatarFile()
    {
        try
        {
            if (string.IsNullOrEmpty(_avatar)) return;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeraphineAITool");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "avatar.txt"), _avatar);
        }
        catch { }
    }

    // ---------------------------------------------------------------------
    // HTTP
    // ---------------------------------------------------------------------

    public static async Task<(bool success, string message, JsonElement? data)> PostAsync(string path, object body)
    {
        try
        {
            var url = ServerUrl.TrimEnd('/') + path;
            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, content);
            var respText = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(respText);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetString() : "0";
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : "";
            var data = root.TryGetProperty("data", out var d) ? d.Clone() : (JsonElement?)null;

            // 保存 token（如果返回了）
            if (root.TryGetProperty("config", out var cfg))
            {
                if (cfg.TryGetProperty("X-Token", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    var tk = t.GetString();
                    if (!string.IsNullOrEmpty(tk)) Token = tk;
                }
                if (cfg.TryGetProperty("X-Refresh", out var r) && r.ValueKind == JsonValueKind.String)
                {
                    var rt = r.GetString();
                    if (!string.IsNullOrEmpty(rt)) RefreshToken = rt;
                }
            }

            return (code == "0", message ?? "", data);
        }
        catch (Exception ex)
        {
            return (false, $"网络错误：{ex.Message}", null);
        }
    }
}
