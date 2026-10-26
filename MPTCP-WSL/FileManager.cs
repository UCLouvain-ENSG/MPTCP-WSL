using System.Management;

namespace MPTCP_WSL;

public class FileManager
{
    private const string AppName = "MPTCP";
    private const string AppContext = "MPTCP Enabler";
    private const string ConfigFileName = "config.json";
    private const string LogFileName = "log.txt";
    private const string WslConfigName = ".wslconfig";
    private const string KernelName = "BzImage";
    private const string InstalledOnFileName = "installedOn";
    private static string? _cachedUserName;

    /// <summary>
    ///     Name of the Windows user whose profile holds the config, the log and .wslconfig.
    ///     The service usually runs as LocalSystem, so the user is read from
    ///     %ProgramData%\MPTCP\installedOn (second line) or, failing that, taken from the interactive session.
    ///     Only a successful lookup is cached, because nobody may be logged on yet when the service starts.
    /// </summary>
    private static string? ResolveUserName()
    {
        if (_cachedUserName != null) return _cachedUserName;
        foreach (var lookup in new Func<string?>[] { ReadInstalledOnUser, GetConsoleUser })
        {
            var candidate = lookup();
            if (candidate != null && Directory.Exists(GetProfileDirectory(candidate)))
                return _cachedUserName = candidate;
        }

        return null;
    }

    private static string? ReadInstalledOnUser()
    {
        try
        {
            var filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                AppName, InstalledOnFileName);
            if (!File.Exists(filePath)) return null;
            var lines = File.ReadAllLines(filePath);
            return lines.Length >= 2 ? ExtractUserName(lines[1]) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? GetConsoleUser()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT UserName FROM Win32_ComputerSystem");
            foreach (ManagementObject computerSystem in searcher.Get())
                using (computerSystem)
                {
                    var userName = computerSystem["UserName"] as string;
                    if (!string.IsNullOrWhiteSpace(userName)) return ExtractUserName(userName);
                }
        }
        catch (Exception)
        {
            // WMI not available (yet): the caller treats this as "no user".
        }

        return null;
    }

    /// <summary>
    ///     Accepts "DOMAIN\user", "C:\Users\user[\...]" or a bare user name.
    /// </summary>
    private static string? ExtractUserName(string value)
    {
        var parts = value.Trim().Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var usersIndex = Array.FindIndex(parts, p => p.Equals("Users", StringComparison.OrdinalIgnoreCase));
        if (usersIndex >= 0 && usersIndex + 1 < parts.Length) return parts[usersIndex + 1];
        return parts[^1];
    }

    private static string GetProfileDirectory(string userName)
    {
        return Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Users", userName);
    }

    public static string? GetConfigPath()
    {
        var userName = ResolveUserName();
        if (userName == null) return null;
        return Path.Combine(GetProfileDirectory(userName), "AppData", "Local", AppName, ConfigFileName);
    }

    /// <summary>
    ///     Never null: falls back to %ProgramData%\MPTCP when no user profile can be found.
    /// </summary>
    public static string GetLogPath()
    {
        var userName = ResolveUserName();
        if (userName == null)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName,
                LogFileName);
        return Path.Combine(GetProfileDirectory(userName), "AppData", "Local", AppName, LogFileName);
    }

    public static string GetKernelPath()
    {
        return Path.Combine(GetApplicationFolderPath(), AppName, AppContext, KernelName);
    }


    private static string GetApplicationFolderPath()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    }

    public static string? GetWSLConfigPath()
    {
        var userName = ResolveUserName();
        if (userName == null) return null;
        return Path.Combine(GetProfileDirectory(userName), WslConfigName);
    }
}
