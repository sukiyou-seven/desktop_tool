using System;
using System.Linq;

namespace DesktopTool;

/// <summary>
/// 用户级 PATH 环境变量管理：添加/移除目录，避免重复。
/// </summary>
public static class EnvVarHelper
{
    private const string PathVar = "PATH";

    /// <summary>将目录前置到用户级 PATH（确保优先级最高，已存在则移到最前）。返回 true 表示实际执行了修改。</summary>
    public static bool AddToUserPath(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        dir = dir.Trim().TrimEnd('\\', '/');
        var current = Environment.GetEnvironmentVariable(PathVar, EnvironmentVariableTarget.User) ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().TrimEnd('\\', '/'))
            .Where(p => !string.Equals(p, dir, StringComparison.OrdinalIgnoreCase))
            .ToList();
        parts.Insert(0, dir);
        var newValue = string.Join(';', parts);
        if (string.Equals(newValue, current, StringComparison.Ordinal))
            return false;
        Environment.SetEnvironmentVariable(PathVar, newValue, EnvironmentVariableTarget.User);
        return true;
    }

    /// <summary>从用户级 PATH 移除目录。返回 true 表示实际执行了移除。</summary>
    public static bool RemoveFromUserPath(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        dir = dir.Trim().TrimEnd('\\', '/');
        var current = Environment.GetEnvironmentVariable(PathVar, EnvironmentVariableTarget.User) ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().TrimEnd('\\', '/'))
            .Where(p => !string.Equals(p, dir, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var newValue = string.Join(';', parts);
        if (string.Equals(newValue, current, StringComparison.Ordinal))
            return false;
        Environment.SetEnvironmentVariable(PathVar, newValue, EnvironmentVariableTarget.User);
        return true;
    }

    /// <summary>检查目录是否已在用户级 PATH 中。</summary>
    public static bool IsInUserPath(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        dir = dir.Trim().TrimEnd('\\', '/');
        var current = Environment.GetEnvironmentVariable(PathVar, EnvironmentVariableTarget.User) ?? "";
        return current.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(p => string.Equals(p.Trim().TrimEnd('\\', '/'), dir, StringComparison.OrdinalIgnoreCase));
    }
}
