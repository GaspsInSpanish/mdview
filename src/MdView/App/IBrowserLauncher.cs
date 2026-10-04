namespace MdView.App;

public interface IBrowserLauncher
{
    bool TryLaunch(string url, out string? error);
    void RequestExit(Action shutdown) => shutdown();
}
