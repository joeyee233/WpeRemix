using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WpeRemix.Core;

public static class InputPicker
{
    private const int Cancelled = unchecked((int)0x800704C7);
    private const uint AddSelectionControl = 1001;
    private const uint SelectionHintControl = 1002;
    private const uint FileSystemPath = 0x80058000;
    private const uint ForceFileSystem = 0x40;
    private const uint AllowMultiSelect = 0x200;
    private const uint PathMustExist = 0x800;
    private const uint FileMustExist = 0x1000;
    private const uint DoNotAddToRecent = 0x02000000;

    public static IReadOnlyList<string> Show(Window owner, string initialDirectory)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.Dispatcher.VerifyAccess();
        var dialog = (OpenDialog)new OpenDialogObject();
        PickerEvents? events = null;
        uint cookie = 0;
        bool advised = false;
        try
        {
            ConfigureDialog(dialog, initialDirectory);
            events = new PickerEvents(dialog);
            dialog.Advise(events, out cookie);
            advised = true;

            int result = dialog.Show(new WindowInteropHelper(owner).EnsureHandle());
            if (result == Cancelled)
            {
                return Array.Empty<string>();
            }
            if (events.Error != null)
            {
                throw new InvalidOperationException("无法读取所选文件或文件夹", events.Error);
            }
            Marshal.ThrowExceptionForHR(result);
            if (events.Paths.Count > 0)
            {
                return events.Paths;
            }
            dialog.GetResults(out ShellItemArray items);
            return ReadPaths(items);
        }
        finally
        {
            if (advised)
            {
                try
                {
                    dialog.Unadvise(cookie);
                }
                catch (COMException)
                {
                    // Releasing the dialog still tears down its event subscription.
                }
            }
            GC.KeepAlive(events);
            Marshal.ReleaseComObject(dialog);
        }
    }

    private static void ConfigureDialog(OpenDialog dialog, string initialDirectory)
    {
        dialog.GetOptions(out uint options);
        dialog.SetOptions(options | ForceFileSystem | AllowMultiSelect | PathMustExist | FileMustExist | DoNotAddToRecent);
        dialog.SetTitle("添加文件或文件夹");
        dialog.SetOkButtonLabel("打开文件");
        var filters = new[]
        {
            new FilterSpec("支持的资源", "*.pkg;*.tex;*.zip;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff"),
            new FilterSpec("所有文件", "*.*")
        };
        dialog.SetFileTypes((uint)filters.Length, filters);
        SetInitialDirectory(dialog, initialDirectory);

        var customize = (DialogCustomize)dialog;
        customize.AddPushButton(AddSelectionControl, "添加所选项");
        customize.AddText(SelectionHintControl, "可同时选择多个文件和文件夹，点击“添加所选项”加入队列");
    }

    private static void SetInitialDirectory(OpenDialog dialog, string initialDirectory)
    {
        if (!Directory.Exists(initialDirectory))
        {
            return;
        }
        Guid shellItemId = typeof(ShellItem).GUID;
        int result = SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero, ref shellItemId, out ShellItem? folder);
        if (result < 0 || folder == null)
        {
            return;
        }
        try
        {
            dialog.SetFolder(folder);
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
        }
    }

    private static IReadOnlyList<string> ReadPaths(ShellItemArray items)
    {
        try
        {
            items.GetCount(out uint count);
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index < count; index++)
            {
                items.GetItemAt(index, out ShellItem item);
                try
                {
                    item.GetDisplayName(FileSystemPath, out IntPtr name);
                    try
                    {
                        string? path = Marshal.PtrToStringUni(name);
                        if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && seen.Add(path))
                        {
                            paths.Add(path);
                        }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(name);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
            return paths;
        }
        finally
        {
            Marshal.ReleaseComObject(items);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out ShellItem? item);

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class PickerEvents : DialogEvents, DialogControlEvents
    {
        private readonly OpenDialog _dialog;

        public PickerEvents(OpenDialog dialog)
        {
            _dialog = dialog;
        }

        public IReadOnlyList<string> Paths { get; private set; } = Array.Empty<string>();
        public Exception? Error { get; private set; }

        public int OnButtonClicked(IntPtr customize, uint controlId)
        {
            if (controlId != AddSelectionControl)
            {
                return 0;
            }
            try
            {
                int result = _dialog.GetSelectedItems(out ShellItemArray? selected);
                if (result < 0 || selected == null)
                {
                    return 0;
                }
                IReadOnlyList<string> paths = ReadPaths(selected);
                if (paths.Count == 0)
                {
                    return 0;
                }
                Paths = paths;
                // Close does not populate GetResults, so keep the selected paths before closing.
                _dialog.Close(0);
            }
            catch (Exception exception)
            {
                Error = exception;
                try
                {
                    _dialog.Close(unchecked((int)0x80004005));
                }
                catch (COMException)
                {
                    // A dialog that is already closing must not leak a callback exception.
                }
            }
            return 0;
        }

        public int OnFileOk(IntPtr dialog)
        {
            return 0;
        }

        public int OnFolderChanging(IntPtr dialog, IntPtr folder)
        {
            return 0;
        }

        public int OnFolderChange(IntPtr dialog)
        {
            return 0;
        }

        public int OnSelectionChange(IntPtr dialog)
        {
            return 0;
        }

        public int OnShareViolation(IntPtr dialog, IntPtr item, out uint response)
        {
            response = 0;
            return 0;
        }

        public int OnTypeChange(IntPtr dialog)
        {
            return 0;
        }

        public int OnOverwrite(IntPtr dialog, IntPtr item, out uint response)
        {
            response = 0;
            return 0;
        }

        public int OnItemSelected(IntPtr customize, uint controlId, uint itemId)
        {
            return 0;
        }

        public int OnCheckButtonToggled(IntPtr customize, uint controlId, bool isChecked)
        {
            return 0;
        }

        public int OnControlActivating(IntPtr customize, uint controlId)
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Name;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Pattern;

        public FilterSpec(string name, string pattern)
        {
            Name = name;
            Pattern = pattern;
        }
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class OpenDialogObject
    {
    }

    // These declarations follow the Windows SDK's inherited vtable order.
    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface OpenDialog
    {
        [PreserveSig]
        int Show(IntPtr owner);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise([MarshalAs(UnmanagedType.Interface)] DialogEvents events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(ShellItem folder);
        void SetFolder(ShellItem folder);
        void GetFolder(out ShellItem folder);
        void GetCurrentSelection(out ShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out ShellItem item);
        void AddPlace(ShellItem item, uint alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid clientId);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        void GetResults(out ShellItemArray items);
        [PreserveSig]
        int GetSelectedItems(out ShellItemArray? items);
    }

    [ComImport]
    [Guid("E6FDD21A-163F-4975-9C8C-A69F1BA37034")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface DialogCustomize
    {
        void EnableOpenDropDown(uint controlId);
        void AddMenu(uint controlId, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void AddPushButton(uint controlId, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void AddComboBox(uint controlId);
        void AddRadioButtonList(uint controlId);
        void AddCheckButton(uint controlId, [MarshalAs(UnmanagedType.LPWStr)] string label, [MarshalAs(UnmanagedType.Bool)] bool isChecked);
        void AddEditBox(uint controlId, [MarshalAs(UnmanagedType.LPWStr)] string text);
        void AddSeparator(uint controlId);
        void AddText(uint controlId, [MarshalAs(UnmanagedType.LPWStr)] string text);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handlerId, ref Guid interfaceId, out IntPtr value);
        void GetParent(out ShellItem parent);
        void GetDisplayName(uint displayName, out IntPtr name);
    }

    [ComImport]
    [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ShellItemArray
    {
        void BindToHandler(IntPtr context, ref Guid handlerId, ref Guid interfaceId, out IntPtr value);
        void GetPropertyStore(uint flags, ref Guid interfaceId, out IntPtr store);
        void GetPropertyDescriptionList(IntPtr key, ref Guid interfaceId, out IntPtr descriptions);
        void GetAttributes(uint flags, uint mask, out uint attributes);
        void GetCount(out uint count);
        void GetItemAt(uint index, out ShellItem item);
    }

    [ComVisible(true)]
    [Guid("973510DB-7D7F-452B-8975-74A85828D354")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface DialogEvents
    {
        [PreserveSig]
        int OnFileOk(IntPtr dialog);
        [PreserveSig]
        int OnFolderChanging(IntPtr dialog, IntPtr folder);
        [PreserveSig]
        int OnFolderChange(IntPtr dialog);
        [PreserveSig]
        int OnSelectionChange(IntPtr dialog);
        [PreserveSig]
        int OnShareViolation(IntPtr dialog, IntPtr item, out uint response);
        [PreserveSig]
        int OnTypeChange(IntPtr dialog);
        [PreserveSig]
        int OnOverwrite(IntPtr dialog, IntPtr item, out uint response);
    }

    [ComVisible(true)]
    [Guid("36116642-D713-4B97-9B83-7484A9D00433")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface DialogControlEvents
    {
        [PreserveSig]
        int OnItemSelected(IntPtr customize, uint controlId, uint itemId);
        [PreserveSig]
        int OnButtonClicked(IntPtr customize, uint controlId);
        [PreserveSig]
        int OnCheckButtonToggled(IntPtr customize, uint controlId, [MarshalAs(UnmanagedType.Bool)] bool isChecked);
        [PreserveSig]
        int OnControlActivating(IntPtr customize, uint controlId);
    }
}
