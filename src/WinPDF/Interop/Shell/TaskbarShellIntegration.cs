using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace WinPDF.Interop.Shell;

/// <summary>
/// Integrates unpackaged WinPDF windows with the Windows 10/11 Taskbar and Jump List:
/// binds a unified <c>AppUserModelID</c>, sets <c>PKEY_AppUserModel_RelaunchCommand</c>
/// (so middle-clicking the taskbar icon launches a new blank instance with <c>--new-window</c>),
/// notifies <c>SHAddToRecentDocs</c>, and builds the custom Recent + Tasks Jump List via <c>ICustomDestinationList</c>.
/// </summary>
internal static partial class TaskbarShellIntegration
{
    /// <summary>
    /// Unified explicit Application User Model ID shared by the process, window property store,
    /// installer shortcuts, and file-type registration.
    /// </summary>
    public const string AppUserModelId = "Fluentify.WinPDF";

    /// <summary>
    /// CLI argument passed when middle-clicking the taskbar icon or choosing "New window"
    /// so the new instance starts without reopening the previous document.
    /// </summary>
    public const string NewWindowArgument = "--new-window";

    private const uint SHARD_PATHW = 0x00000003;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const ushort VT_LPWSTR = 31;

    private static readonly Guid CLSID_DestinationList = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");
    private static readonly Guid CLSID_EnumerableObjectCollection = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");
    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");

    private static readonly Guid IID_IObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");
    private static readonly Guid IID_IShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    private static readonly Guid FMTID_AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private static readonly Guid FMTID_SummaryInformation = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");

    private static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchCommand = new() { fmtid = FMTID_AppUserModel, pid = 2 };
    private static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchIconResource = new() { fmtid = FMTID_AppUserModel, pid = 3 };
    private static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchDisplayNameResource = new() { fmtid = FMTID_AppUserModel, pid = 4 };
    private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new() { fmtid = FMTID_AppUserModel, pid = 5 };
    private static readonly PROPERTYKEY PKEY_Title = new() { fmtid = FMTID_SummaryInformation, pid = 2 };

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr pwszVal;
        public IntPtr extra;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appID);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        in Guid riid,
        out IntPtr ppv);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHAddToRecentDocs(uint uFlags, string pv);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PROPVARIANT pvar);

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(in PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(in PROPERTYKEY key, in PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [ComImport]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        [PreserveSig] int GetCount(out uint pcObjects);
        [PreserveSig] int GetAt(uint uiIndex, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
    }

    [ComImport]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        // IObjectArray
        [PreserveSig] int GetCount(out uint pcObjects);
        [PreserveSig] int GetAt(uint uiIndex, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);

        // IObjectCollection
        [PreserveSig] int AddObject([MarshalAs(UnmanagedType.IUnknown)] object punk);
        [PreserveSig] int AddFromArray(IObjectArray poaSource);
        [PreserveSig] int RemoveObjectAt(uint uiIndex);
        [PreserveSig] int Clear();
    }

    [ComImport]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        [PreserveSig] int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        [PreserveSig] int BeginList(out uint pcMinSlots, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
        [PreserveSig] int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, IObjectArray poa);
        [PreserveSig] int AppendKnownCategory(int category);
        [PreserveSig] int AddUserTasks(IObjectArray poa);
        [PreserveSig] int CommitList();
        [PreserveSig] int GetRemovedDestinations(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
        [PreserveSig] int DeleteList([MarshalAs(UnmanagedType.LPWStr)] string? pszAppID);
        [PreserveSig] int AbortList();
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        [PreserveSig] int GetHotkey(out ushort pwHotkey);
        [PreserveSig] int SetHotkey(ushort wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    /// <summary>
    /// Registers the explicit process-wide AppUserModelID before any window or taskbar item is created.
    /// </summary>
    public static void ApplyProcessAppUserModelId()
    {
        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Configures taskbar grouping and middle-click relaunch properties on the main window <c>HWND</c>.
    /// Must be called before <c>Window.Activate()</c>.
    /// </summary>
    /// <param name="hwnd">Native window handle.</param>
    public static void ConfigureWindowProperties(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return;
        }

        IPropertyStore? store = null;
        IntPtr storePtr = IntPtr.Zero;
        try
        {
            Guid iid = IID_IPropertyStore;
            if (SHGetPropertyStoreForWindow(hwnd, in iid, out storePtr) != 0 || storePtr == IntPtr.Zero)
            {
                return;
            }

            store = Marshal.GetObjectForIUnknown(storePtr) as IPropertyStore;
            Marshal.Release(storePtr);
            storePtr = IntPtr.Zero;
            if (store is null)
            {
                return;
            }

            SetStringProperty(store, PKEY_AppUserModel_RelaunchCommand, $"\"{exePath}\" {NewWindowArgument}");
            SetStringProperty(store, PKEY_AppUserModel_RelaunchDisplayNameResource, "WinPDF");
            SetStringProperty(store, PKEY_AppUserModel_RelaunchIconResource, $"{exePath},0");
            SetStringProperty(store, PKEY_AppUserModel_ID, AppUserModelId);
            _ = store.Commit();
        }
        catch
        {
        }
        finally
        {
            if (storePtr != IntPtr.Zero)
            {
                _ = Marshal.Release(storePtr);
            }

            if (store is not null)
            {
                _ = Marshal.ReleaseComObject(store);
            }
        }
    }

    /// <summary>
    /// Notifies the Windows Shell that a PDF file was opened so native Shell recent tracking stays up to date.
    /// </summary>
    /// <param name="filePath">Full path to the opened PDF file.</param>
    public static void NotifyRecentFileOpened(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            SHAddToRecentDocs(SHARD_PATHW, filePath);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Rebuilds the taskbar Jump List asynchronously on a transient STA thread so it never stalls the UI thread
    /// and preserves zero idle CPU/GPU usage.
    /// </summary>
    /// <param name="recentFilePaths">Ordered recent PDF file paths (newest first).</param>
    /// <param name="recentCategoryTitle">Localized category header (e.g. "Recent" / "Recentes").</param>
    /// <param name="newWindowTaskTitle">Localized task label (e.g. "New window" / "Nova janela").</param>
    /// <param name="onUserRemovedPath">Optional callback invoked if the user manually removed an entry from the Jump List.</param>
    public static void RefreshJumpListAsync(
        IReadOnlyList<string> recentFilePaths,
        string recentCategoryTitle,
        string newWindowTaskTitle,
        Action<string>? onUserRemovedPath = null)
    {
        List<string> snapshot = [.. recentFilePaths];
        Thread thread = new(() =>
        {
            RefreshJumpListCore(snapshot, recentCategoryTitle, newWindowTaskTitle, onUserRemovedPath);
        })
        {
            IsBackground = true,
            Name = "WinPDF.JumpListUpdater",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void RefreshJumpListCore(
        IReadOnlyList<string> recentFilePaths,
        string recentCategoryTitle,
        string newWindowTaskTitle,
        Action<string>? onUserRemovedPath)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return;
        }

        ICustomDestinationList? destList = null;
        IObjectCollection? recentCollection = null;
        IObjectCollection? tasksCollection = null;
        object? removedObj = null;

        try
        {
            Type? destType = Type.GetTypeFromCLSID(CLSID_DestinationList);
            if (destType is null || Activator.CreateInstance(destType) is not ICustomDestinationList list)
            {
                return;
            }

            destList = list;
            if (destList.SetAppID(AppUserModelId) != 0)
            {
                return;
            }

            Guid oaGuid = IID_IObjectArray;
            if (destList.BeginList(out uint minSlots, in oaGuid, out removedObj) != 0)
            {
                return;
            }

            HashSet<string> removedPaths = new(StringComparer.OrdinalIgnoreCase);
            if (removedObj is IObjectArray removedArray && removedArray.GetCount(out uint removedCount) == 0)
            {
                for (uint i = 0; i < removedCount; i++)
                {
                    if (TryExtractPathFromDestination(removedArray, i, out string? removedPath) &&
                        !string.IsNullOrWhiteSpace(removedPath))
                    {
                        removedPaths.Add(removedPath);
                        onUserRemovedPath?.Invoke(removedPath);
                    }
                }
            }

            string pdfIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "pdf-document.ico");
            if (!File.Exists(pdfIconPath))
            {
                pdfIconPath = exePath;
            }

            Type? collType = Type.GetTypeFromCLSID(CLSID_EnumerableObjectCollection);
            if (collType is null)
            {
                _ = destList.AbortList();
                return;
            }

            int maxRecent = Math.Clamp((int)minSlots, 5, 10);
            if (Activator.CreateInstance(collType) is IObjectCollection rc)
            {
                recentCollection = rc;
                int added = 0;
                foreach (string path in recentFilePaths)
                {
                    if (added >= maxRecent)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(path) || removedPaths.Contains(path) || !File.Exists(path))
                    {
                        continue;
                    }

                    string fileName = Path.GetFileName(path);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    if (CreateShellLink(exePath, $"\"{path}\"", fileName, path, pdfIconPath) is { } link)
                    {
                        _ = recentCollection.AddObject(link);
                        Marshal.ReleaseComObject(link);
                        added++;
                    }
                }

                if (added > 0)
                {
                    _ = destList.AppendCategory(recentCategoryTitle, (IObjectArray)recentCollection);
                }
            }

            if (Activator.CreateInstance(collType) is IObjectCollection tc)
            {
                tasksCollection = tc;
                if (CreateShellLink(exePath, NewWindowArgument, newWindowTaskTitle, newWindowTaskTitle, exePath) is { } taskLink)
                {
                    _ = tasksCollection.AddObject(taskLink);
                    Marshal.ReleaseComObject(taskLink);
                    _ = destList.AddUserTasks((IObjectArray)tasksCollection);
                }
            }

            _ = destList.CommitList();
        }
        catch
        {
            if (destList is not null)
            {
                try { _ = destList.AbortList(); } catch { }
            }
        }
        finally
        {
            if (removedObj is not null && Marshal.IsComObject(removedObj))
            {
                Marshal.ReleaseComObject(removedObj);
            }

            if (recentCollection is not null)
            {
                Marshal.ReleaseComObject(recentCollection);
            }

            if (tasksCollection is not null)
            {
                Marshal.ReleaseComObject(tasksCollection);
            }

            if (destList is not null)
            {
                Marshal.ReleaseComObject(destList);
            }
        }
    }

    private static bool TryExtractPathFromDestination(IObjectArray array, uint index, out string? path)
    {
        path = null;
        Guid shellLinkGuid = IID_IShellLinkW;
        if (array.GetAt(index, in shellLinkGuid, out object? linkObj) == 0 && linkObj is IShellLinkW link)
        {
            try
            {
                System.Text.StringBuilder args = new(1024);
                if (link.GetArguments(args, args.Capacity) == 0)
                {
                    string trimmed = args.ToString().Trim('\"', ' ');
                    if (!string.IsNullOrWhiteSpace(trimmed) && !string.Equals(trimmed, NewWindowArgument, StringComparison.OrdinalIgnoreCase))
                    {
                        path = trimmed;
                        return true;
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }

        Guid shellItemGuid = IID_IShellItem;
        if (array.GetAt(index, in shellItemGuid, out object? itemObj) == 0 && itemObj is IShellItem item)
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                if (item.GetDisplayName(SIGDN_FILESYSPATH, out ptr) == 0 && ptr != IntPtr.Zero)
                {
                    path = Marshal.PtrToStringUni(ptr);
                    return !string.IsNullOrWhiteSpace(path);
                }
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(ptr);
                }

                Marshal.ReleaseComObject(item);
            }
        }

        return false;
    }

    private static IShellLinkW? CreateShellLink(
        string exePath,
        string arguments,
        string title,
        string description,
        string iconPath)
    {
        Type? linkType = Type.GetTypeFromCLSID(CLSID_ShellLink);
        if (linkType is null || Activator.CreateInstance(linkType) is not IShellLinkW link)
        {
            return null;
        }

        try
        {
            _ = link.SetPath(exePath);
            _ = link.SetArguments(arguments);
            _ = link.SetDescription(description);
            _ = link.SetIconLocation(iconPath, 0);

            string? workDir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrWhiteSpace(workDir))
            {
                _ = link.SetWorkingDirectory(workDir);
            }

            if (link is IPropertyStore propStore)
            {
                SetStringProperty(propStore, PKEY_Title, title);
                _ = propStore.Commit();
            }

            return link;
        }
        catch
        {
            Marshal.ReleaseComObject(link);
            return null;
        }
    }

    private static void SetStringProperty(IPropertyStore store, PROPERTYKEY key, string value)
    {
        PROPVARIANT pv = new()
        {
            vt = VT_LPWSTR,
            pwszVal = Marshal.StringToCoTaskMemUni(value),
        };
        try
        {
            _ = store.SetValue(in key, in pv);
        }
        finally
        {
            _ = PropVariantClear(ref pv);
        }
    }
}
