using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WpeRemix.Core;

internal static class KnownFolders
{
    internal static string Downloads
    {
        get
        {
            var folderId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            IntPtr folderPath = IntPtr.Zero;
            try
            {
                int result = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out folderPath);
                if (result >= 0)
                {
                    string? path = Marshal.PtrToStringUni(folderPath);
                    if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                    {
                        return path;
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(folderPath);
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
