using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using Renci.SshNet;
using MySqlConnector;
using Npgsql;
using MongoDB.Driver;
using StackExchange.Redis;

namespace DesktopTool;

public enum DbType { MySql, Redis, MongoDB, PostgreSQL, SQLite }

/// <summary>数据库连接参数。</summary>
public class DbConnection
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public string Username { get; set; } = "root";
    public string Password { get; set; } = "";
    /// <summary>MongoDB 认证数据库（authSource），默认 admin。</summary>
    public string AuthSource { get; set; } = "admin";
}

/// <summary>数据库条目。</summary>
public class DbEntry
{
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>
/// 通过 SSH 隧道 + C# 数据库驱动管理远程服务器上的数据库。
/// 安装/卸载/启停仍用 SSH 命令；查询/增删改密用驱动。
/// </summary>
public static class DatabaseManager
{
    /// <summary>把异常（含内部异常）拍平成一行可读文本。</summary>
    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        var e = ex;
        int depth = 0;
        while (e is not null && depth < 5)
        {
            var msg = (e.Message ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            parts.Add($"{e.GetType().Name}: {msg}");
            e = e.InnerException;
            depth++;
        }
        return string.Join(" | ", parts);
    }

    private static (int Code, string Out, string Err) Run(SshClient c, string cmd)
        => SshService.Execute(c, cmd);

    // ---------- 基础信息 ----------

    public static string DetectPkgMgr(SshClient c)
    {
        if (Run(c, "which apt-get").Code == 0) return "apt";
        if (Run(c, "which dnf").Code == 0) return "dnf";
        if (Run(c, "which yum").Code == 0) return "yum";
        return "unknown";
    }

    public static int DefaultPort(DbType t) => t switch
    {
        DbType.MySql => 3306,
        DbType.Redis => 6379,
        DbType.MongoDB => 27017,
        DbType.PostgreSQL => 5432,
        _ => 0,
    };

    public static string DefaultUser(DbType t) => t switch
    {
        DbType.MySql => "root",
        DbType.Redis => "",
        DbType.MongoDB => "root",
        DbType.PostgreSQL => "postgres",
        _ => "",
    };

    private static string[] ServiceCandidates(DbType t) => t switch
    {
        DbType.MySql => new[] { "mysql", "mysqld", "mariadb" },
        DbType.Redis => new[] { "redis-server", "redis" },
        DbType.MongoDB => new[] { "mongod", "mongodb" },
        DbType.PostgreSQL => new[] { "postgresql", "postgres" },
        _ => Array.Empty<string>(),
    };

    private static string[] BinaryCandidates(DbType t) => t switch
    {
        DbType.MySql => new[] { "mysql", "mysqld", "mariadb" },
        DbType.Redis => new[] { "redis-server", "redis-cli", "redis", "/www/server/redis/src/redis-server", "/www/server/redis/src/redis-cli" },
        DbType.MongoDB => new[] { "mongod", "mongosh", "mongo" },
        DbType.PostgreSQL => new[] { "psql", "postgres", "postgresql" },
        DbType.SQLite => new[] { "sqlite3" },
        _ => Array.Empty<string>(),
    };

    private static string? ProcessName(DbType t) => t switch
    {
        DbType.MySql => "mysqld",
        DbType.Redis => "redis-server",
        DbType.MongoDB => "mongod",
        DbType.PostgreSQL => "postgres",
        _ => null,
    };

    private static string? FindService(SshClient c, DbType t)
    {
        foreach (var svc in ServiceCandidates(t))
        {
            var r = Run(c, $"systemctl list-unit-files '{svc}.service' 2>/dev/null | grep -i '{svc}'");
            if (r.Code == 0 && !string.IsNullOrWhiteSpace(r.Out)) return svc;
        }
        var proc = ProcessName(t);
        if (proc is not null)
        {
            var r = Run(c, $"pgrep -x '{proc}' >/dev/null 2>&1");
            if (r.Code == 0) return ServiceCandidates(t).FirstOrDefault();
        }
        return null;
    }

    private static string PackageName(DbType t, string pkg) => t switch
    {
        DbType.MySql => "mysql-server",
        DbType.Redis => pkg == "apt" ? "redis-server" : "redis",
        DbType.MongoDB => "mongodb-org",
        DbType.PostgreSQL => pkg == "apt" ? "postgresql" : "postgresql-server",
        DbType.SQLite => pkg == "apt" ? "sqlite3" : "sqlite",
        _ => "",
    };

    public static string DetectHostIp(SshClient c)
    {
        try
        {
            var wsl = Run(c, "grep -qi microsoft /proc/version 2>/dev/null && echo WSL");
            if (wsl.Out.Trim() == "WSL")
            {
                var ns = Run(c, "grep nameserver /etc/resolv.conf 2>/dev/null | awk '{print $2}' | head -1");
                if (!string.IsNullOrWhiteSpace(ns.Out)) return ns.Out.Trim();
                var gw = Run(c, "ip route show default 2>/dev/null | awk '{print $3}' | head -1");
                if (!string.IsNullOrWhiteSpace(gw.Out)) return gw.Out.Trim();
            }
        }
        catch { }
        return "127.0.0.1";
    }

    public static bool IsPortListening(SshClient c, int port)
    {
        var r = Run(c, $"ss -tlnp 2>/dev/null | grep -q ':{port} ' && echo YES || (netstat -tlnp 2>/dev/null | grep -q ':{port} ' && echo YES)");
        return r.Out.Trim() == "YES";
    }

    // ---------- 安装检测（SSH 命令） ----------

    public static bool IsInstalled(SshClient c, DbType t)
    {
        foreach (var bin in BinaryCandidates(t))
        {
            var r = Run(c, $"command -v '{bin}' 2>/dev/null");
            if (r.Code == 0 && !string.IsNullOrWhiteSpace(r.Out)) return true;
        }
        var pkgKeyword = t switch
        {
            DbType.Redis => "redis",
            DbType.MongoDB => "mongo",
            DbType.MySql => "mysql",
            DbType.PostgreSQL => "postgres",
            DbType.SQLite => "sqlite",
            _ => "",
        };
        if (!string.IsNullOrEmpty(pkgKeyword))
        {
            var dpkg = Run(c, $"dpkg -l 2>/dev/null | grep -i '{pkgKeyword}' | head -1");
            if (dpkg.Code == 0 && !string.IsNullOrWhiteSpace(dpkg.Out)) return true;
            var rpm = Run(c, $"rpm -qa 2>/dev/null | grep -i '{pkgKeyword}' | head -1");
            if (rpm.Code == 0 && !string.IsNullOrWhiteSpace(rpm.Out)) return true;
        }
        if (IsPortListening(c, DefaultPort(t))) return true;
        var paths = t switch
        {
            DbType.Redis => new[] { "/usr/bin/redis-server", "/usr/local/bin/redis-server", "/opt/redis/redis-server", "/www/server/redis/redis-server", "/www/server/redis/src/redis-server", "/snap/bin/redis-server" },
            DbType.MongoDB => new[] { "/usr/bin/mongod", "/usr/local/bin/mongod", "/www/server/mongodb/bin/mongod", "/snap/bin/mongod" },
            DbType.MySql => new[] { "/usr/sbin/mysqld", "/www/server/mysql/bin/mysqld" },
            _ => Array.Empty<string>(),
        };
        foreach (var p in paths)
        {
            var r = Run(c, $"test -x '{p}' && echo ok");
            if (r.Code == 0) return true;
        }
        return false;
    }

    // ---------- SSH 隧道 ----------

    /// <summary>通过 SSH 本地端口转发建立隧道，在回调中使用本地端口。</summary>
    private static T WithTunnel<T>(SshClient ssh, string remoteHost, int remotePort, Func<int, T> action)
    {
        if (!ssh.IsConnected)
            throw new InvalidOperationException("SSH 连接已断开，请重新连接服务器。");

        // 找一个空闲本地端口
        TcpListener? listener = null;
        int localPort;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally { listener?.Stop(); }

        ForwardedPortLocal? forwarded = null;
        try
        {
            forwarded = new ForwardedPortLocal("127.0.0.1", (uint)localPort, remoteHost, (uint)remotePort);
            ssh.AddForwardedPort(forwarded);
            forwarded.Start();
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(50);
                if (forwarded.IsStarted) break;
            }
            if (!forwarded.IsStarted)
                throw new InvalidOperationException("SSH 隧道启动失败（端口转发未就绪）。");
        }
        catch (Exception ex)
        {
            try { forwarded?.Stop(); } catch { }
            try { if (forwarded is not null) ssh.RemoveForwardedPort(forwarded); } catch { }
            throw new InvalidOperationException($"SSH 隧道建立失败({remoteHost}:{remotePort}): {ex.Message}", ex);
        }

        try
        {
            return action(localPort);
        }
        finally
        {
            try { forwarded?.Stop(); } catch { }
            try { if (forwarded is not null) ssh.RemoveForwardedPort(forwarded); } catch { }
        }
    }

    // ---------- MongoDB 辅助 ----------

    private static string BuildMongoUrl(DbConnection conn, int localPort)
    {
        if (string.IsNullOrEmpty(conn.Password))
            return $"mongodb://127.0.0.1:{localPort}/?directConnection=true&serverSelectionTimeoutMS=8000&connectTimeoutMS=8000";
        return $"mongodb://{Uri.EscapeDataString(conn.Username)}:{Uri.EscapeDataString(conn.Password)}@127.0.0.1:{localPort}/?authSource={Uri.EscapeDataString(conn.AuthSource)}&directConnection=true&serverSelectionTimeoutMS=8000&connectTimeoutMS=8000";
    }

    // ---------- 连接测试（驱动） ----------

    public static string TestConnection(SshClient c, DbType t, DbConnection conn)
    {
        if (t == DbType.SQLite) return "SQLite 为文件型数据库，无需连接。";
        try
        {
            return t switch
            {
                DbType.MySql => TestMySql(c, conn),
                DbType.Redis => TestRedis(c, conn),
                DbType.MongoDB => TestMongo(c, conn),
                DbType.PostgreSQL => TestPostgres(c, conn),
                _ => "不支持",
            };
        }
        catch (Exception ex) { return $"连接失败：{Flatten(ex)}"; }
    }

    private static string TestMySql(SshClient c, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var cs = $"Server=127.0.0.1;Port={localPort};User ID={conn.Username};Password={conn.Password};Connection Timeout=5;";
            using var myConn = new MySqlConnection(cs);
            myConn.Open();
            using var cmd = myConn.CreateCommand();
            cmd.CommandText = "SELECT VERSION()";
            return $"连接成功，MySQL 版本：{cmd.ExecuteScalar()}";
        });
    }

    private static string TestRedis(SshClient c, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var config = string.IsNullOrEmpty(conn.Password)
                ? $"127.0.0.1:{localPort},connectTimeout=5000,abortConnect=false"
                : $"127.0.0.1:{localPort},password={conn.Password},connectTimeout=5000,abortConnect=false";
            using var redis = ConnectionMultiplexer.Connect(config);
            var db = redis.GetDatabase();
            var pong = db.Ping();
            return $"连接成功，Redis 响应 PONG（延迟 {pong.TotalMilliseconds:F0}ms）";
        });
    }

    private static string TestMongo(SshClient c, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var client = new MongoClient(BuildMongoUrl(conn, localPort));
            var db = client.GetDatabase("admin");
            var version = db.RunCommand<MongoDB.Bson.BsonDocument>("{ buildInfo: 1 }");
            return $"连接成功，MongoDB 版本：{version["version"]}";
        });
    }

    private static string TestPostgres(SshClient c, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var cs = $"Host=127.0.0.1;Port={localPort};Username={conn.Username};Password={conn.Password};Database=postgres;Timeout=5;";
            using var pgConn = new NpgsqlConnection(cs);
            pgConn.Open();
            using var cmd = pgConn.CreateCommand();
            cmd.CommandText = "SELECT version()";
            return $"连接成功：{cmd.ExecuteScalar()?.ToString()?.Split('\n')[0]}";
        });
    }

    // ---------- 安装 / 卸载（SSH 命令） ----------

    public static string Install(SshClient c, DbType t)
    {
        var pkg = DetectPkgMgr(c);
        if (pkg == "unknown") return "未识别的包管理器（apt/dnf/yum），请手动安装。";
        var name = PackageName(t, pkg);
        string installCmd = pkg switch
        {
            "apt" => $"DEBIAN_FRONTEND=noninteractive apt-get update -qq && apt-get install -y {name}",
            "dnf" => $"dnf install -y {name}",
            "yum" => $"yum install -y {name}",
            _ => "",
        };
        if (t == DbType.PostgreSQL && pkg != "apt")
            installCmd += " && postgresql-setup --initdb 2>/dev/null; systemctl enable postgresql";
        if (t == DbType.MongoDB)
            return "MongoDB 官方源配置较复杂，建议先手动添加 MongoDB 源后再安装，或使用系统源中的 mongodb 包。";
        var r = Run(c, installCmd);
        return $"exit={r.Code}\n{r.Out}\n{r.Err}".Trim();
    }

    public static string Uninstall(SshClient c, DbType t)
    {
        var pkg = DetectPkgMgr(c);
        if (pkg == "unknown") return "未识别的包管理器。";
        var name = PackageName(t, pkg);
        string cmd = pkg switch
        {
            "apt" => $"apt-get remove -y --purge {name} && apt-get autoremove -y",
            "dnf" => $"dnf remove -y {name}",
            "yum" => $"yum remove -y {name}",
            _ => "",
        };
        var r = Run(c, cmd);
        return $"exit={r.Code}\n{r.Out}\n{r.Err}".Trim();
    }

    // ---------- 启停（SSH 命令） ----------

    public static bool IsRunning(SshClient c, DbType t)
    {
        if (t == DbType.SQLite) return true;
        foreach (var svc in ServiceCandidates(t))
        {
            var r = Run(c, $"systemctl is-active '{svc}' 2>/dev/null");
            if (r.Out.Trim() == "active") return true;
        }
        var proc = ProcessName(t);
        if (proc is not null)
        {
            var r = Run(c, $"pgrep -x '{proc}' >/dev/null 2>&1");
            if (r.Code == 0) return true;
        }
        return false;
    }

    public static string Start(SshClient c, DbType t)
    {
        if (t == DbType.SQLite) return "SQLite 无服务进程。";
        var svc = FindService(c, t);
        if (svc is null) return "未找到 systemd 服务，可能是手动编译安装，请手动启动。";
        var r = Run(c, $"systemctl start '{svc}' && systemctl enable '{svc}' 2>/dev/null");
        return r.Code == 0 ? "已启动" : $"启动失败：{r.Err}";
    }

    public static string Stop(SshClient c, DbType t)
    {
        if (t == DbType.SQLite) return "SQLite 无服务进程。";
        var svc = FindService(c, t);
        if (svc is null) return "未找到 systemd 服务。";
        var r = Run(c, $"systemctl stop '{svc}'");
        return r.Code == 0 ? "已停止" : $"停止失败：{r.Err}";
    }

    public static string Restart(SshClient c, DbType t)
    {
        if (t == DbType.SQLite) return "SQLite 无服务进程。";
        var svc = FindService(c, t);
        if (svc is null) return "未找到 systemd 服务。";
        var r = Run(c, $"systemctl restart '{svc}'");
        return r.Code == 0 ? "已重启" : $"重启失败：{r.Err}";
    }

    // ---------- 数据库列表（驱动） ----------

    public static List<DbEntry> ListDatabases(SshClient c, DbType t, DbConnection conn)
    {
        try
        {
            return t switch
            {
                DbType.MySql => ListMySql(c, conn),
                DbType.Redis => ListRedis(c, conn),
                DbType.MongoDB => ListMongo(c, conn),
                DbType.PostgreSQL => ListPostgres(c, conn),
                DbType.SQLite => ListSqlite(c),
                _ => new List<DbEntry>(),
            };
        }
        catch (Exception ex) { return new List<DbEntry> { new() { Name = "查询失败", Detail = Flatten(ex) } }; }
    }

    private static List<DbEntry> ListMySql(SshClient c, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var cs = $"Server=127.0.0.1;Port={localPort};User ID={conn.Username};Password={conn.Password};";
            using var myConn = new MySqlConnection(cs);
            myConn.Open();
            using var cmd = myConn.CreateCommand();
            cmd.CommandText = "SHOW DATABASES";
            using var reader = cmd.ExecuteReader();
            var list = new List<DbEntry>();
            while (reader.Read())
                list.Add(new DbEntry { Name = reader.GetString(0) });
            return list;
        });
    }

    private static List<DbEntry> ListRedis(SshClient c, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var config = string.IsNullOrEmpty(conn.Password)
                ? $"127.0.0.1:{localPort},connectTimeout=5000,abortConnect=false"
                : $"127.0.0.1:{localPort},password={conn.Password},connectTimeout=5000,abortConnect=false";
            using var redis = ConnectionMultiplexer.Connect(config);
            var db = redis.GetDatabase();

            var list = new List<DbEntry>();
            try
            {
                var info = db.Execute("INFO", "keyspace").ToString() ?? "";
                var found = new HashSet<int>();
                foreach (Match m in Regex.Matches(info, @"db(\d+):keys=(\d+)"))
                {
                    if (int.TryParse(m.Groups[1].Value, out var idx))
                    {
                        found.Add(idx);
                        list.Add(new DbEntry { Name = $"db{idx}", Detail = $"{m.Groups[2].Value} keys" });
                    }
                }
                // 补充空库（最多 16 个）
                for (int i = 0; i < 16; i++)
                    if (!found.Contains(i))
                        list.Add(new DbEntry { Name = $"db{i}", Detail = "空" });
                list.Sort((a, b) =>
                {
                    int ai = int.TryParse(a.Name.AsSpan(2), out var x) ? x : 0;
                    int bi = int.TryParse(b.Name.AsSpan(2), out var y) ? y : 0;
                    return ai.CompareTo(bi);
                });
            }
            catch (Exception ex)
            {
                list.Add(new DbEntry { Name = "读取失败", Detail = $"{ex.GetType().Name}: {ex.Message}" });
            }
            return list;
        });
    }

    private static List<DbEntry> ListMongo(SshClient c, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var client = new MongoClient(BuildMongoUrl(conn, localPort));
            var names = client.ListDatabaseNames().ToList();
            return names.Select(n => new DbEntry { Name = n }).ToList();
        });
    }

    private static List<DbEntry> ListPostgres(SshClient c, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var cs = $"Host=127.0.0.1;Port={localPort};Username={conn.Username};Password={conn.Password};Database=postgres;";
            using var pgConn = new NpgsqlConnection(cs);
            pgConn.Open();
            using var cmd = pgConn.CreateCommand();
            cmd.CommandText = "SELECT datname FROM pg_database WHERE datistemplate=false ORDER BY datname";
            using var reader = cmd.ExecuteReader();
            var list = new List<DbEntry>();
            while (reader.Read())
                list.Add(new DbEntry { Name = reader.GetString(0) });
            return list;
        });
    }

    private static List<DbEntry> ListSqlite(SshClient c)
    {
        var r = Run(c, "find /root /opt /home -maxdepth 3 -type f \\( -name '*.db' -o -name '*.sqlite' -o -name '*.sqlite3' \\) 2>/dev/null");
        return r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new DbEntry { Name = Path.GetFileName(p.Trim()), Detail = p.Trim() })
            .ToList();
    }

    // ---------- 新增 / 删除（驱动） ----------

    public static string CreateDatabase(SshClient c, DbType t, string name, DbConnection conn)
    {
        try
        {
            return t switch
            {
                DbType.MySql => CreateMySql(c, name, conn),
                DbType.Redis => "Redis 无命名数据库，直接 SELECT 数字索引即可使用。",
                DbType.MongoDB => CreateMongo(c, name, conn),
                DbType.PostgreSQL => CreatePostgres(c, name, conn),
                DbType.SQLite => Run(c, $"sqlite3 /root/{name}.db \".tables\" 2>&1").Out.Trim(),
                _ => "不支持",
            };
        }
        catch (Exception ex) { return $"创建失败：{Flatten(ex)}"; }
    }

    private static string CreateMySql(SshClient c, string name, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var cs = $"Server=127.0.0.1;Port={localPort};User ID={conn.Username};Password={conn.Password};";
            using var myConn = new MySqlConnection(cs);
            myConn.Open();
            using var cmd = myConn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE `{name}`";
            cmd.ExecuteNonQuery();
            return $"已创建数据库：{name}";
        });
    }

    private static string CreateMongo(SshClient c, string name, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var client = new MongoClient(BuildMongoUrl(conn, localPort));
            client.GetDatabase(name).CreateCollection("init");
            return $"已创建数据库：{name}";
        });
    }

    private static string CreatePostgres(SshClient c, string name, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var cs = $"Host=127.0.0.1;Port={localPort};Username={conn.Username};Password={conn.Password};Database=postgres;";
            using var pgConn = new NpgsqlConnection(cs);
            pgConn.Open();
            using var cmd = pgConn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{name}\"";
            cmd.ExecuteNonQuery();
            return $"已创建数据库：{name}";
        });
    }

    public static string DropDatabase(SshClient c, DbType t, string name, DbConnection conn)
    {
        try
        {
            return t switch
            {
                DbType.MySql => DropMySql(c, name, conn),
                DbType.Redis => DropRedis(c, name, conn),
                DbType.MongoDB => DropMongo(c, name, conn),
                DbType.PostgreSQL => DropPostgres(c, name, conn),
                DbType.SQLite => Run(c, $"rm -f /root/{name} 2>&1").Out.Trim(),
                _ => "不支持",
            };
        }
        catch (Exception ex) { return $"删除失败：{Flatten(ex)}"; }
    }

    private static string DropMySql(SshClient c, string name, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var cs = $"Server=127.0.0.1;Port={localPort};User ID={conn.Username};Password={conn.Password};";
            using var myConn = new MySqlConnection(cs);
            myConn.Open();
            using var cmd = myConn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE `{name}`";
            cmd.ExecuteNonQuery();
            return $"已删除数据库：{name}";
        });
    }

    private static string DropRedis(SshClient c, string name, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var config = string.IsNullOrEmpty(conn.Password)
                ? $"127.0.0.1:{localPort},connectTimeout=5000,abortConnect=false"
                : $"127.0.0.1:{localPort},password={conn.Password},connectTimeout=5000,abortConnect=false";
            using var redis = ConnectionMultiplexer.Connect(config);
            var dbNum = int.TryParse(Regex.Match(name, @"\d+").Value, out var n) ? n : 0;
            var db = redis.GetDatabase(dbNum);
            db.Execute("FLUSHDB");
            return $"已清空 {name}";
        });
    }

    private static string DropMongo(SshClient c, string name, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var client = new MongoClient(BuildMongoUrl(conn, localPort));
            client.DropDatabase(name);
            return $"已删除数据库：{name}";
        });
    }

    private static string DropPostgres(SshClient c, string name, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var cs = $"Host=127.0.0.1;Port={localPort};Username={conn.Username};Password={conn.Password};Database=postgres;";
            using var pgConn = new NpgsqlConnection(cs);
            pgConn.Open();
            using var cmd = pgConn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE \"{name}\"";
            cmd.ExecuteNonQuery();
            return $"已删除数据库：{name}";
        });
    }

    // ---------- 改密（驱动） ----------

    public static string ChangePassword(SshClient c, DbType t, string user, string newPass, DbConnection conn, string? database = null)
    {
        try
        {
            return t switch
            {
                DbType.MySql => ChangeMySql(c, user, newPass, conn),
                DbType.Redis => ChangeRedis(c, newPass, conn),
                DbType.MongoDB => ChangeMongo(c, user, newPass, conn, database),
                DbType.PostgreSQL => ChangePostgres(c, user, newPass, conn),
                DbType.SQLite => "SQLite 无用户密码体系。",
                _ => "不支持",
            };
        }
        catch (Exception ex) { return $"改密失败：{Flatten(ex)}"; }
    }

    private static string ChangeMySql(SshClient c, string user, string newPass, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var cs = $"Server=127.0.0.1;Port={localPort};User ID={conn.Username};Password={conn.Password};";
            using var myConn = new MySqlConnection(cs);
            myConn.Open();
            // 先查用户存在哪些 host，再逐个改密
            using var query = myConn.CreateCommand();
            query.CommandText = "SELECT DISTINCT Host FROM mysql.user WHERE User=@user";
            query.Parameters.AddWithValue("@user", user);
            var hosts = new List<string>();
            using var reader = query.ExecuteReader();
            while (reader.Read())
                hosts.Add(reader.GetString(0));
            reader.Close();

            if (hosts.Count == 0)
                return $"未找到用户：{user}";

            int ok = 0;
            foreach (var h in hosts)
            {
                try
                {
                    using var cmd = myConn.CreateCommand();
                    cmd.CommandText = $"ALTER USER '{user}'@'{h}' IDENTIFIED BY @pwd";
                    cmd.Parameters.AddWithValue("@pwd", newPass);
                    cmd.ExecuteNonQuery();
                    ok++;
                }
                catch { }
            }
            using var flush = myConn.CreateCommand();
            flush.CommandText = "FLUSH PRIVILEGES";
            flush.ExecuteNonQuery();
            return $"密码已修改：{user}（{ok}/{hosts.Count} 个 host）";
        });
    }

    private static string ChangeRedis(SshClient c, string newPass, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var config = string.IsNullOrEmpty(conn.Password)
                ? $"127.0.0.1:{localPort},connectTimeout=5000,abortConnect=false"
                : $"127.0.0.1:{localPort},password={conn.Password},connectTimeout=5000,abortConnect=false";
            using var redis = ConnectionMultiplexer.Connect(config);
            var db = redis.GetDatabase();
            db.Execute("CONFIG", "SET", "requirepass", newPass);
            return "Redis 密码已修改（注意：需写入配置文件才能持久化）";
        });
    }

    private static string ChangeMongo(SshClient c, string user, string newPass, DbConnection conn, string? database)
    {
        var dbName = string.IsNullOrWhiteSpace(database) ? conn.AuthSource : database;
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var url = BuildMongoUrl(conn, localPort);
            var client = new MongoClient(url);
            var db = client.GetDatabase(dbName);
            // MongoDB 用 updateUser 命令修改密码（changeUserPassword 在部分版本不存在）
            var cmd = new MongoDB.Bson.BsonDocument
            {
                { "updateUser", user },
                { "pwd", newPass }
            };
            db.RunCommand<MongoDB.Bson.BsonDocument>(cmd);
            return $"密码已修改：{user}（库 {dbName}）";
        });
    }

    private static string ChangePostgres(SshClient c, string user, string newPass, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var cs = $"Host=127.0.0.1;Port={localPort};Username={conn.Username};Password={conn.Password};Database=postgres;";
            using var pgConn = new NpgsqlConnection(cs);
            pgConn.Open();
            using var cmd = pgConn.CreateCommand();
            cmd.CommandText = $"ALTER USER \"{user}\" WITH PASSWORD '{newPass}'";
            cmd.ExecuteNonQuery();
            return $"密码已修改：{user}";
        });
    }

    // ---------- 列出用户 ----------

    public static List<string> ListUsers(SshClient c, DbType t, DbConnection conn, string? database = null)
    {
        try
        {
            return t switch
            {
                DbType.MongoDB => ListMongoUsers(c, conn, database),
                DbType.MySql => ListMySqlUsers(c, conn),
                DbType.PostgreSQL => ListPostgresUsers(c, conn),
                _ => new List<string>(),
            };
        }
        catch { return new List<string>(); }
    }

    private static List<string> ListMongoUsers(SshClient c, DbConnection conn, string? database)
    {
        var dbName = string.IsNullOrWhiteSpace(database) ? conn.AuthSource : database;
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var client = new MongoClient(BuildMongoUrl(conn, localPort));
            var db = client.GetDatabase(dbName);
            var result = db.RunCommand<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument { { "usersInfo", 1 } });
            var users = new List<string>();
            if (result.TryGetValue("users", out var arr) && arr.IsBsonArray)
                foreach (var u in arr.AsBsonArray)
                    if (u.IsBsonDocument && u.AsBsonDocument.TryGetValue("user", out var name))
                    {
                        var s = name.ToString();
                        if (!string.IsNullOrEmpty(s)) users.Add(s);
                    }
            return users;
        });
    }

    private static List<string> ListMySqlUsers(SshClient c, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var cs = $"Server=127.0.0.1;Port={localPort};User ID={conn.Username};Password={conn.Password};";
            using var myConn = new MySqlConnection(cs);
            myConn.Open();
            using var cmd = myConn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT User FROM mysql.user ORDER BY User";
            var users = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) users.Add(reader.GetString(0));
            return users;
        });
    }

    private static List<string> ListPostgresUsers(SshClient c, DbConnection conn)
    {
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var cs = $"Host=127.0.0.1;Port={localPort};Username={conn.Username};Password={conn.Password};Database=postgres;";
            using var pgConn = new NpgsqlConnection(cs);
            pgConn.Open();
            using var cmd = pgConn.CreateCommand();
            cmd.CommandText = "SELECT usename FROM pg_user ORDER BY usename";
            var users = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) users.Add(reader.GetString(0));
            return users;
        });
    }

    // ---------- 创建用户 ----------

    /// <summary>
    /// 创建数据库用户并授权。用当前连接参数（管理员）的权限执行。
    /// database 参数：MongoDB 为用户所在库（authSource），MySQL/PostgreSQL 为授权目标库。
    /// </summary>
    public static string CreateUser(SshClient c, DbType t, string username, string password, string database, DbConnection conn)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return "用户名和密码不能为空";
        try
        {
            return t switch
            {
                DbType.MongoDB => CreateMongoUser(c, username, password, database, conn),
                DbType.MySql => CreateMySqlUser(c, username, password, database, conn),
                DbType.PostgreSQL => CreatePostgresUser(c, username, password, database, conn),
                DbType.Redis => "Redis 无用户体系，直接使用改密设置全局密码",
                DbType.SQLite => "SQLite 无用户体系",
                _ => "不支持",
            };
        }
        catch (Exception ex) { return $"创建用户失败：{Flatten(ex)}"; }
    }

    private static string CreateMongoUser(SshClient c, string username, string password, string database, DbConnection conn)
    {
        var dbName = string.IsNullOrWhiteSpace(database) ? conn.AuthSource : database;
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var client = new MongoClient(BuildMongoUrl(conn, localPort));
            var db = client.GetDatabase(dbName);
            var cmd = new MongoDB.Bson.BsonDocument
            {
                { "createUser", username },
                { "pwd", password },
                { "roles", new MongoDB.Bson.BsonArray
                    {
                        new MongoDB.Bson.BsonDocument { { "role", "readWrite" }, { "db", dbName } }
                    }
                }
            };
            db.RunCommand<MongoDB.Bson.BsonDocument>(cmd);
            return $"用户已创建：{username}（库 {dbName}，角色 readWrite）";
        });
    }

    private static string CreateMySqlUser(SshClient c, string username, string password, string database, DbConnection conn)
    {
        var dbName = string.IsNullOrWhiteSpace(database) ? "*" : database;
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var cs = $"Server=127.0.0.1;Port={localPort};User ID={conn.Username};Password={conn.Password};";
            using var myConn = new MySqlConnection(cs);
            myConn.Open();
            using var cmd = myConn.CreateCommand();
            cmd.CommandText = $"CREATE USER IF NOT EXISTS '{username}'@'%' IDENTIFIED BY @pwd; " +
                              $"GRANT ALL PRIVILEGES ON `{dbName}`.* TO '{username}'@'%'; " +
                              $"FLUSH PRIVILEGES;";
            cmd.Parameters.AddWithValue("@pwd", password);
            // MySqlConnector 需逐条执行
            var statements = cmd.CommandText.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var sql in statements)
            {
                using var c2 = myConn.CreateCommand();
                c2.CommandText = sql.Trim();
                if (sql.Contains("@pwd"))
                    c2.Parameters.AddWithValue("@pwd", password);
                c2.ExecuteNonQuery();
            }
            return $"用户已创建：{username}（授权 {dbName}）";
        });
    }

    private static string CreatePostgresUser(SshClient c, string username, string password, string database, DbConnection conn)
    {
        var dbName = string.IsNullOrWhiteSpace(database) ? "postgres" : database;
        return WithTunnel(c, conn.Host, conn.Port, localPort =>
        {
            var cs = $"Host=127.0.0.1;Port={localPort};Username={conn.Username};Password={conn.Password};Database=postgres;";
            using var pgConn = new NpgsqlConnection(cs);
            pgConn.Open();
            using var cmd = pgConn.CreateCommand();
            cmd.CommandText = $"CREATE USER \"{username}\" WITH PASSWORD '{password}'";
            cmd.ExecuteNonQuery();
            if (dbName != "postgres")
            {
                using var grant = pgConn.CreateCommand();
                grant.CommandText = $"GRANT ALL PRIVILEGES ON DATABASE \"{dbName}\" TO \"{username}\"";
                grant.ExecuteNonQuery();
            }
            return $"用户已创建：{username}（授权 {dbName}）";
        });
    }
}
