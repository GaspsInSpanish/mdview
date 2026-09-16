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
                if (await coordinator.TryForwardAsync(path, TimeSpan.FromSeconds(3)).ConfigureAwait(false))
                {
                    return 0;
                }

                ShowError("The running mdview instance did not respond. Close it in Task Manager, then try again.");
                return 1;
            }

            using var server = new ReaderServer(fileDialogs: new NativeFileDialogProvider());
            server.Start();
            var document = server.RegisterDocument(path);
            coordinator.WriteHandshake(server.Port);

            IBrowserLauncher browserLauncher = new BraveLauncher();
            using var lifecycle = new Lifecycle(server, coordinator, browserLauncher, reportMessage: ShowWarning);
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
}
