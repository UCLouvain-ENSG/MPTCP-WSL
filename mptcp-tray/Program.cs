namespace mptcp_tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One tray icon per user session.
        using var mutex = new Mutex(true, @"Local\MPTCP-Tray", out var first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        using var app = new TrayApp();
        Application.Run();
    }
}
