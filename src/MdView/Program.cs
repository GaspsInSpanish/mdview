using System.Runtime.InteropServices;
using MdView.App;
using MdView.Serving;

namespace MdView;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (!TryValidatePath(args, out var path, out var validationError))
            {
                ShowError(validationError);
                return 1;
            }

            using var coordinator = new InstanceCoordinator();
            if (!coordinator.IsPrimary)
            {
                _ = AllowSetForegroundWindow(unchecked((uint)-1));
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                var connected = false;
                do
                {
                    var result = await HandOffPipeClient.TrySendDetailedAsync(path).ConfigureAwait(false);
                    connected |= result != HandOffResult.Unavailable;
                    if (result == HandOffResult.Accepted) return 0;
                    if (coordinator.TryBecomePrimary()) break;
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        ShowError(connected
                            ? "Another mdview is running but did not accept the file. If you just upgraded, close the old mdview and try again."
                            : "Another mdview is running and did not respond. If you just upgraded, close the old mdview and try again.");
                        return 1;
                    }
                    await Task.Delay(remaining < TimeSpan.FromMilliseconds(150)
                        ? remaining : TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
                }
                while (true);
            }

            coordinator.DeleteLegacyHandshake();
            var host = new DocumentHost(() => DocumentHost.Origin, fileDialogs: new NativeFileDialogProvider());
            var document = host.RegisterDocument(path);
            Lifecycle? lifecycle = null;
            var handOffServer = new HandOffPipeServer(host, accept: opened => lifecycle?.TryAcceptHandOff(opened) == true,
                reportError: ShowWarning);

            var browserLauncher = new WebViewWindowLauncher(host, ShowWarning);
            lifecycle = new Lifecycle(host, handOffServer, browserLauncher, coordinator, browserLauncher,
                gracePeriod: TimeSpan.Zero, reportMessage: ShowWarning);
            using var lifecycleOwner = lifecycle;
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                lifecycle.Shutdown();
            };

            if (!lifecycle.TryLaunch(document))
            {
                return 1;
            }

            await lifecycle.Completion.ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            ShowError($"mdview could not start: {exception.Message}");
            return 1;
        }
    }

    private static bool TryValidatePath(string[] args, out string path, out string error)
    {
        path = string.Empty;
        if (args.Length != 1)
        {
            error = "mdview expects exactly one Markdown file path.";
            return false;
        }

        path = Path.GetFullPath(args[0]);
        if (!File.Exists(path))
        {
            error = $"The file does not exist: {path}";
            return false;
        }

        if (!DocumentKinds.TryGetKind(path, out _))
        {
            error = $"This file type is not supported: {Path.GetExtension(path)}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static void ShowError(string message) => ShowMessage(message, 0x10);
    private static void ShowWarning(string message) => ShowMessage(message, 0x30);

    private static void ShowMessage(string message, uint icon) =>
        MessageBox(IntPtr.Zero, message, "mdview", icon | 0x00000000);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
