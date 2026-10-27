using MPTCP_WSL;
using Newtonsoft.Json;
using Serilog;

public class NetworkConfig
{
    private readonly object _sync = new();
    public bool ManageEndpoint = true;
    public bool ManageKernelLocation = true;

    public NetworkConfig()
    {
        Config = new List<NetworkInformation>();
    }


    public List<NetworkInformation> Config { get; set; } = new();
    public Proxy Proxy { get; set; } = new();
    // Credentials required by the proxy listening on 127.0.0.1:1080 (only user and password are used).
    public Proxy LocalProxy { get; set; } = new();
    public int SubflowNr { get; set; } = 2;
    public int AddAddrAcceptedNr { get; set; } = 4;

    public void NetworkMonitorOnOnUpdate(object? sender, CollectionUpdateEvent e)
    {
        if (e.Type != EventType.Addition) return;
        lock (_sync)
        {
            if (Config.Contains(e.NetworkInfo)) return;
            Config.Add(e.NetworkInfo);
            SaveConfigToFile();
        }
    }


    public void SaveConfigToFile()
    {
        try
        {
            var cfgPath = FileManager.GetConfigPath();
            if (cfgPath == null) return;
            lock (_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cfgPath)!);
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                // Write then move so the Linux side never reads a half-written file.
                var tmpPath = cfgPath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, cfgPath, true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unable to save the configuration file");
        }
    }

    public static NetworkConfig LoadConfigFromFile()
    {
        try
        {
            var cfgPath = FileManager.GetConfigPath();
            if (cfgPath != null && File.Exists(cfgPath))
            {
                var json = File.ReadAllText(cfgPath);
                var config = JsonConvert.DeserializeObject<NetworkConfig>(json);
                if (config == null) return new NetworkConfig();
                return config;
            }

            return new NetworkConfig();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unable to read the configuration file, using defaults");
            return new NetworkConfig();
        }
    }
}

public class NetworkInformation : IEquatable<NetworkInformation>
{
    public string InterfaceName { get; set; }
    public string FriendlyInterfaceName { get; set; }
    public string WindowsMacAddress { get; set; }
    public string LinuxMacAddress { get; set; }
    public List<string> Types { get; set; } = new();

    public bool Equals(NetworkInformation? other)
    {
        return other != null &&
               string.Equals(WindowsMacAddress, other.WindowsMacAddress, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as NetworkInformation);
    }

    public override int GetHashCode()
    {
        return WindowsMacAddress?.ToUpperInvariant().GetHashCode() ?? 0;
    }

    public NetworkInformation Clone()
    {
        return new NetworkInformation
        {
            InterfaceName = InterfaceName,
            FriendlyInterfaceName = FriendlyInterfaceName,
            WindowsMacAddress = WindowsMacAddress,
            LinuxMacAddress = LinuxMacAddress,
            Types = new List<string>(Types)
        };
    }
}

public class Proxy
{
    public string password = "";
    public string proxyAddress = "";
    public string proxyPort = "";
    public string user = "";
}
