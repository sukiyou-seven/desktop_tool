using System;
using System.Runtime.InteropServices;

namespace DesktopTool;

/// <summary>
/// Windows 凭据管理器（Credential Manager）读写辅助。
/// 凭据保存到系统级凭据库，即使本软件关闭或被卸载仍然有效；
/// 且键名采用 git 的 Git Credential Manager 约定（git:https://&lt;host&gt;），
/// 因此 git 命令行（clone/push 私有仓库）可直接复用同一份凭据。
/// </summary>
public static class WindowsCredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const uint CRED_PERSIST_ENTERPRISE = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    /// <summary>保存 / 覆盖一条通用凭据（永久持久化到本机凭据库）。</summary>
    public static void Save(string target, string userName, string secret)
    {
        var secretBytes = System.Text.Encoding.Unicode.GetBytes(secret);
        var cred = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            Persist = CRED_PERSIST_LOCAL_MACHINE,
            TargetName = Marshal.StringToCoTaskMemUni(target),
            UserName = Marshal.StringToCoTaskMemUni(userName),
            CredentialBlobSize = (uint)secretBytes.Length,
            CredentialBlob = Marshal.AllocCoTaskMem(secretBytes.Length),
        };
        try
        {
            Marshal.Copy(secretBytes, 0, cred.CredentialBlob, secretBytes.Length);
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException($"写入凭据失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
        }
        finally
        {
            Marshal.FreeCoTaskMem(cred.TargetName);
            Marshal.FreeCoTaskMem(cred.UserName);
            Marshal.FreeCoTaskMem(cred.CredentialBlob);
        }
    }

    /// <summary>读取一条通用凭据；不存在返回 false。</summary>
    public static bool TryRead(string target, out string? userName, out string? secret)
    {
        userName = null;
        secret = null;
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr)) return false;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            userName = Marshal.PtrToStringUni(cred.UserName);
            if (cred.CredentialBlobSize > 0)
                secret = Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(ptr);
        }
        return secret is not null;
    }

    /// <summary>删除一条通用凭据；不存在视为成功。</summary>
    public static void Delete(string target)
    {
        CredDelete(target, CRED_TYPE_GENERIC, 0);
    }

    /// <summary>Git 凭据目标名（与 Git Credential Manager 一致，git 可直接复用）。</summary>
    public static string GitTarget(string host) => $"git:https://{host}";
}
