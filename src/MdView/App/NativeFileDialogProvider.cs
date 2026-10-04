using System.Runtime.InteropServices;
using System.Text;
using MdView.Serving;

namespace MdView.App;

public sealed class NativeFileDialogProvider : IFileDialogProvider
{
    private static readonly Guid FileOpenDialogClass = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid FileSaveDialogClass = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
    private static readonly Guid FileDialogInterface = new("42F85136-DB7E-439C-85F1-E4075D135FC8");
    private const int CancelledHResult = unchecked((int)0x800704C7);
    private const uint ClsctxInprocServer = 0x1;
    private const uint CoinitApartmentThreaded = 0x2;
    private const uint FosOverwritePrompt = 0x2;
    private const uint FosForceFileSystem = 0x40;
    private const uint FosPathMustExist = 0x800;
    private const uint FosFileMustExist = 0x1000;
    private const uint SigdnFileSystemPath = 0x80058000;

    public Task<string?> ShowOpenAsync(string initialDirectory, CancellationToken cancellationToken) =>
        RunStaAsync(() => ShowDialog(FileOpenDialogClass, initialDirectory, null, save: false), cancellationToken);

    public Task<string?> ShowSaveAsAsync(string initialDirectory, string suggestedFileName,
        CancellationToken cancellationToken) =>
        RunStaAsync(() => ShowDialog(FileSaveDialogClass, initialDirectory, suggestedFileName, save: true),
            cancellationToken);

    private static Task<string?> RunStaAsync(Func<string?> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = false;
            try
            {
                ThrowIfFailed(CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded));
                initialized = true;
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(action());
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                if (initialized) CoUninitialize();
            }
        }) { IsBackground = true, Name = "mdview file dialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static string? ShowDialog(Guid classId, string initialDirectory, string? suggestedFileName, bool save)
    {
        ThrowIfFailed(CoCreateInstance(classId, IntPtr.Zero, ClsctxInprocServer, FileDialogInterface, out var dialog));
        try
        {
            dialog.GetOptions(out var options);
            options |= FosForceFileSystem | FosPathMustExist;
            options |= save ? FosOverwritePrompt : FosFileMustExist;
            dialog.SetOptions(options);
            var filters = new[]
            {
                new FilterSpec("Markdown files", "*.md;*.markdown"),
                new FilterSpec("All files", "*.*")
            };
            dialog.SetFileTypes((uint)filters.Length, filters);
            dialog.SetFileTypeIndex(1);
            dialog.SetDefaultExtension("md");
            if (!string.IsNullOrWhiteSpace(suggestedFileName)) dialog.SetFileName(suggestedFileName);
            SetInitialFolder(dialog, initialDirectory);

            var owner = FindMdViewForegroundWindow();
            using var foreground = owner == IntPtr.Zero ? BeginForegroundAssist() : null;
            var result = dialog.Show(owner);
            if (result == CancelledHResult) return null;
            ThrowIfFailed(result);
            dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(SigdnFileSystemPath, out var pathPointer);
                try { return Marshal.PtrToStringUni(pathPointer); }
                finally { Marshal.FreeCoTaskMem(pathPointer); }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    private static void SetInitialFolder(IFileDialog dialog, string initialDirectory)
    {
        if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory)) return;
        var shellItemId = typeof(IShellItem).GUID;
        ThrowIfFailed(SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero, shellItemId, out var folder));
        try { dialog.SetFolder(folder); }
        finally { Marshal.ReleaseComObject(folder); }
    }

    private static IntPtr FindMdViewForegroundWindow()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return IntPtr.Zero;
        _ = GetWindowThreadProcessId(window, out var processId);
        return processId == Environment.ProcessId ? window : IntPtr.Zero;
    }

    private static IDisposable BeginForegroundAssist()
    {
        var cancellation = new CancellationTokenSource();
        var threadId = GetCurrentThreadId();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    _ = EnumThreadWindows(threadId, (window, parameter) =>
                    {
                        var className = new StringBuilder(64);
                        _ = GetClassName(window, className, className.Capacity);
                        if (className.ToString() == "#32770" && IsWindowVisible(window))
                        {
                            _ = BringWindowToTop(window);
                            _ = SetForegroundWindow(window);
                            return false;
                        }
                        return true;
                    }, IntPtr.Zero);
                    await Task.Delay(50, cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        });
        return new CancellationScope(cancellation);
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct FilterSpec(string name, string specification)
    {
        [MarshalAs(UnmanagedType.LPWStr)] public readonly string Name = name;
        [MarshalAs(UnmanagedType.LPWStr)] public readonly string Specification = specification;
    }

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem folder, int placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int errorCode);
        void SetClientGuid(in Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, in Guid handlerId, in Guid interfaceId, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint nameType, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    private sealed class CancellationScope(CancellationTokenSource cancellation) : IDisposable
    {
        public void Dispose()
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IFileDialog dialog);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrencyModel);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, in Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
