using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace DesktopTool;

/// <summary>
/// 统一配置同步层（纯云端模式）。
/// 所有配置只存服务器：登录后从服务端拉取到内存缓存，写入时更新内存并推送服务端。
/// 不写入任何本地文件；未登录 / 离线时配置仅本次会话内有效（重启即失）。
/// 登录后会自动把旧版本地缓存文件一次性迁移到服务端并删除本地文件。
/// </summary>
public static class ConfigSync
{
    private static Dictionary<string, JsonElement>? _cache;

    private static Dictionary<string, JsonElement> Cache
        => _cache ??= new Dictionary<string, JsonElement>();

    /// <summary>
    /// 登录后调用：先将旧版本地缓存迁移到服务端（成功后删除本地文件），
    /// 再从服务端拉取全部配置到内存。
    /// </summary>
    public static async Task SyncFromServerAsync()
    {
        if (!ApiHelper.IsLoggedIn) return;

        await MigrateLegacyLocalFileAsync();

        var (success, _, data) = await ApiHelper.PostAsync("/desktop/config/get_all", new
        {
            user_id = ApiHelper.UserId,
            tokens = new { token = ApiHelper.Token ?? "", refresh = ApiHelper.RefreshToken ?? "" },
        });

        if (success && data.HasValue)
        {
            try
            {
                var merged = new Dictionary<string, JsonElement>();
                foreach (var prop in data.Value.EnumerateObject())
                {
                    merged[prop.Name] = prop.Value.Clone();
                }
                _cache = merged;
            }
            catch { }
        }
    }

    /// <summary>读取配置，反序列化为指定类型。</summary>
    public static T? Get<T>(string key, T? fallback = default)
    {
        if (Cache.TryGetValue(key, out var el))
        {
            try
            {
                return el.Deserialize<T>();
            }
            catch { }
        }
        return fallback;
    }

    /// <summary>读取原始 JsonElement。</summary>
    public static JsonElement? GetRaw(string key)
    {
        return Cache.TryGetValue(key, out var el) ? el.Clone() : null;
    }

    /// <summary>写入配置（更新内存缓存并推送服务端；未登录时仅本次会话内有效）。</summary>
    public static void Set<T>(string key, T value)
    {
        try
        {
            Cache[key] = JsonSerializer.SerializeToElement(value);
        }
        catch { }

        // 推送服务端（已登录时）
        if (ApiHelper.IsLoggedIn)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ApiHelper.PostAsync("/desktop/config/set", new
                    {
                        user_id = ApiHelper.UserId,
                        key,
                        value,
                        tokens = new { token = ApiHelper.Token ?? "", refresh = ApiHelper.RefreshToken ?? "" },
                    });
                }
                catch { }
            });
        }
    }

    /// <summary>删除配置。</summary>
    public static void Delete(string key)
    {
        Cache.Remove(key);

        if (ApiHelper.IsLoggedIn)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ApiHelper.PostAsync("/desktop/config/delete", new
                    {
                        user_id = ApiHelper.UserId,
                        key,
                        tokens = new { token = ApiHelper.Token ?? "", refresh = ApiHelper.RefreshToken ?? "" },
                    });
                }
                catch { }
            });
        }
    }

    /// <summary>退出登录时清空内存缓存。</summary>
    public static void Reset()
    {
        _cache = null;
    }

    /// <summary>
    /// 一次性迁移：旧版本把配置缓存写入 %LOCALAPPDATA%\SeraphineAITool\config_sync.json，
    /// 首次登录后把该文件内容推送到服务端并删除本地文件；推送失败则保留文件，下次登录重试。
    /// </summary>
    private static async Task MigrateLegacyLocalFileAsync()
    {
        try
        {
            var legacyFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeraphineAITool", "config_sync.json");
            if (!File.Exists(legacyFile)) return;

            var json = File.ReadAllText(legacyFile);
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (dict is null || dict.Count == 0)
            {
                File.Delete(legacyFile);
                return;
            }

            foreach (var kv in dict)
            {
                var (ok, _, _) = await ApiHelper.PostAsync("/desktop/config/set", new
                {
                    user_id = ApiHelper.UserId,
                    key = kv.Key,
                    value = kv.Value,
                    tokens = new { token = ApiHelper.Token ?? "", refresh = ApiHelper.RefreshToken ?? "" },
                });
                if (!ok) return; // 迁移失败：保留本地文件，避免数据丢失
            }

            File.Delete(legacyFile);
        }
        catch { }
    }
}
