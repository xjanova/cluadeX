using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CluadeX.Services.Helpers;

/// <summary>
/// Native Windows folder picker dialog using IFileOpenDialog COM interface.
/// Does not require System.Windows.Forms.
/// </summary>
public static class FolderPicker
{
    public static string? ShowDialog(string? title = null, string? initialDirectory = null)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();

        try
        {
            dialog.SetOptions(FOS.FOS_PICKFOLDERS | FOS.FOS_FORCEFILESYSTEM);

            if (title != null)
                dialog.SetTitle(title);

            if (initialDirectory != null && System.IO.Directory.Exists(initialDirectory))
            {
                SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero,
                    typeof(IShellItem).GUID, out var folderItem);
                if (folderItem != null)
                    dialog.SetFolder(folderItem);
            }

            var hwnd = Application.Current?.MainWindow != null
                ? new WindowInteropHelper(Application.Current.MainWindow).Handle
                : IntPtr.Zero;

            var hr = dialog.Show(hwnd);
            if (hr != 0) return null; // User cancelled

            dialog.GetResult(out var item);
            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
            return path;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItem ppv);

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes();
        void SetFileTypeIndex();
        void GetFileTypeIndex();
        void Advise();
        void Unadvise();
        void SetOptions(FOS fos);
        void GetOptions();
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder();
        void GetCurrentSelection();
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName();
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel();
        void SetFileNameLabel();
        void GetResult(out IShellItem ppsi);
        void AddPlace();
        void SetDefaultExtension();
        void Close();
        void SetClientGuid();
        void ClearClientData();
        void SetFilter();
        void GetResults();
        void GetSelectedItems();
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler();
        void GetParent();
        void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes();
        void Compare();
    }

    [Flags]
    private enum FOS : uint
    {
        FOS_PICKFOLDERS = 0x00000020,
        FOS_FORCEFILESYSTEM = 0x00000040,
    }

    private enum SIGDN : uint
    {
        SIGDN_FILESYSPATH = 0x80058000,
    }
}
