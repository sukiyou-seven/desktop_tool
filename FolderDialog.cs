using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopTool;

/// <summary>
/// 目录选择对话框（Win32 IFileOpenDialog 封装）。
/// WinUI 3 的 FolderPicker 不支持指定初始目录，此实现可传入 initialPath，
/// 让对话框直接定位到项目目录，便于选择。
/// </summary>
public static class FolderDialog
{
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    /// <summary>弹出目录选择对话框，返回所选目录；取消返回 null。initialPath 存在时自动定位。</summary>
    public static string? PickFolder(IntPtr ownerHwnd, string? initialPath = null)
    {
        return Pick(ownerHwnd, initialPath, "选择目录", pickFolders: true, "所有文件", "*.*");
    }

    /// <summary>弹出文件选择对话框，返回所选文件；取消返回 null。initialPath 存在时自动定位。</summary>
    public static string? PickFile(IntPtr ownerHwnd, string? initialPath = null, string filterName = "所有文件", string filterSpec = "*.*")
    {
        return Pick(ownerHwnd, initialPath, "选择文件", pickFolders: false, filterName, filterSpec);
    }

    private static string? Pick(IntPtr ownerHwnd, string? initialPath, string title, bool pickFolders, string filterName, string filterSpec)
    {
        try
        {
            const uint FOS_FILEMUSTEXIST = 0x00001000;
            var dialog = (IFileOpenDialog)new FileOpenDialogRCW();
            var options = pickFolders ? FOS_PICKFOLDERS : FOS_FILEMUSTEXIST;
            dialog.SetOptions(options);
            dialog.SetTitle(title);

            if (!pickFolders && !string.IsNullOrEmpty(filterSpec))
            {
                dialog.SetFileTypes(1, new[] { new COMDLG_FILTERSPEC { pszName = filterName, pszSpec = filterSpec } });
            }

            if (!string.IsNullOrEmpty(initialPath))
            {
                // 若 initialPath 是目录，直接定位；若是文件，定位到其所在目录
                var dir = Directory.Exists(initialPath) ? initialPath : Path.GetDirectoryName(initialPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    var riid = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(dir, IntPtr.Zero, ref riid, out var item) == 0
                        && item is IShellItem shellItem)
                    {
                        dialog.SetFolder(shellItem);
                        Marshal.ReleaseComObject(shellItem);
                    }
                }
                if (!pickFolders && File.Exists(initialPath))
                {
                    dialog.SetFileName(Path.GetFileName(initialPath));
                }
            }

            if (dialog.Show(ownerHwnd) != 0) return null;

            dialog.GetResult(out var result);
            result.GetDisplayName(SIGDN_FILESYSPATH, out var psz);
            var path = Marshal.PtrToStringUni(psz);
            Marshal.FreeCoTaskMem(psz);
            return path;
        }
        catch
        {
            return null;
        }
    }

    // ---------------- COM Interop ----------------

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRCW { }

    /// <summary>IFileOpenDialog : IFileDialog : IModalWindow（方法顺序 = vtable 顺序，不可乱序）。</summary>
    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr parent);

        // IFileDialog
        void SetFileTypes(uint cFileTypes, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] COMDLG_FILTERSPEC[] rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName(out IntPtr pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, uint fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);

        // IFileOpenDialog
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
}
