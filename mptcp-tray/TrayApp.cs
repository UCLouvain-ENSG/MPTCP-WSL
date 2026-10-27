using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace mptcp_tray;

/// <summary>
///     Tray icon for the MPTCP Enabler service: shows its state, starts and stops it, and exposes the few settings
///     worth changing by hand. It runs as the logged-on user, which is also why it (and not the service, which runs as
///     Local System) is the one that keeps the user's WSL2 VM alive.
/// </summary>
internal sealed class TrayApp : IDisposable
{
    private const string ServiceName = "MPTCPEnabler";
    private const string ProxyHost = "127.0.0.1";
    private const int ProxyPort = 1080;

    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MPTCP");

    private static readonly string ConfigPath = Path.Combine(DataDir, "config.json");
    private static readonly string LogPath = Path.Combine(DataDir, "log.txt");
    private static readonly string TrayPath = Path.Combine(DataDir, "tray.json");

    private static readonly string WslConfigPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");

    private readonly Dictionary<State, Icon> _icons = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _miService = Label();
    private readonly ToolStripMenuItem _miWsl = Label();
    private readonly ToolStripMenuItem _miProxy = Label();
    private readonly ToolStripMenuItem _miNics = Label();
    private readonly ToolStripMenuItem _miToggle = new();
    private readonly ToolStripMenuItem _miRestart = new("Restart service");
    private readonly ToolStripMenuItem _miMode = new("MPTCP mode");
    private readonly ToolStripMenuItem _miKeepAwake = new("Keep WSL running") { CheckOnClick = true };
    private readonly ToolStripMenuItem _miKernel = new("Service sets the WSL kernel (next start)") { CheckOnClick = true };
    private readonly ToolStripMenuItem _miEndpoints = new("Service manages MPTCP endpoints (next start)") { CheckOnClick = true };
    private readonly ToolStripMenuItem _miLocalAuth = new("Proxy requires a login (restart mptcp-wsl)") { CheckOnClick = true };

    private Process? _awake;
    private bool _probing;
    private Snapshot _last = new(null, null, false, false, Array.Empty<string>());

    public TrayApp()
    {
        foreach (var (state, color) in new[]
                 {
                     (State.Ok, Color.FromArgb(46, 160, 67)),
                     (State.Degraded, Color.FromArgb(219, 140, 20)),
                     (State.Down, Color.FromArgb(200, 50, 50))
                 })
            _icons[state] = MakeIcon(color);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _miService, _miWsl, _miProxy, _miNics,
            new ToolStripSeparator(),
            _miToggle, _miRestart,
            new ToolStripSeparator(),
            _miMode, _miKeepAwake, _miKernel, _miEndpoints, _miLocalAuth,
            Item($"Copy proxy address ({ProxyHost}:{ProxyPort})", () => Clipboard.SetText($"{ProxyHost}:{ProxyPort}")),
            new ToolStripSeparator(),
            Item("Open config.json", () => Edit(ConfigPath)),
            Item("Open log", () => Edit(LogPath)),
            Item("Open .wslconfig", () => Edit(WslConfigPath)),
            Item("Restart WSL (wsl --shutdown)", () => Run("wsl.exe", "--shutdown")),
            new ToolStripSeparator(),
            Item("Exit", ExitApp)
        });
        menu.Opening += (_, _) => RefreshMenu();

        _miToggle.Click += (_, _) => ToggleService();
        _miRestart.Click += (_, _) => RunElevated($"sc stop {ServiceName} & timeout /t 3 /nobreak >nul & sc start {ServiceName}");
        _miKeepAwake.Click += (_, _) =>
        {
            SaveTraySetting("KeepWSL2Awake", _miKeepAwake.Checked);
            EnsureAwake();
        };
        _miKernel.Click += (_, _) => SaveConfigBool("ManageKernelLocation", _miKernel.Checked);
        _miEndpoints.Click += (_, _) => SaveConfigBool("ManageEndpoint", _miEndpoints.Checked);
        _miLocalAuth.Click += (_, _) => SaveConfigBool("LocalProxyRequireAuth", _miLocalAuth.Checked);

        _notifyIcon = new NotifyIcon { Icon = _icons[State.Down], Text = "MPTCP Enabler", ContextMenuStrip = menu, Visible = true };
        // A left click shows the same menu as a right click.
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(_notifyIcon, null);
        };

        _timer = new System.Windows.Forms.Timer { Interval = 5000 };
        _timer.Tick += async (_, _) => await Refresh();
        _timer.Start();
        EnsureAwake();
        _ = Refresh();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        StopAwake();
        foreach (var icon in _icons.Values) icon.Dispose();
    }

    private async Task Refresh()
    {
        if (_probing) return;
        _probing = true;
        try
        {
            _last = await Task.Run(Probe);
            EnsureAwake();
            var state = _last.Service != ServiceControllerStatus.Running ? State.Down
                : _last.Proxy ? State.Ok
                : State.Degraded;
            _notifyIcon.Icon = _icons[state];
            // NotifyIcon.Text is limited to 127 characters.
            _notifyIcon.Text = state switch
            {
                State.Ok => "MPTCP: running, proxy up",
                State.Degraded => "MPTCP: service running, proxy not reachable",
                _ => "MPTCP: service " + Describe(_last.Service)
            };
        }
        finally
        {
            _probing = false;
        }
    }

    private static Snapshot Probe()
    {
        ServiceControllerStatus? service = null;
        ServiceStartMode? startMode = null;
        try
        {
            using var sc = new ServiceController(ServiceName);
            service = sc.Status;
            startMode = sc.StartType;
        }
        catch (InvalidOperationException)
        {
            // Not installed.
        }

        var wsl = Process.GetProcessesByName("vmmemWSL").Length > 0 || Process.GetProcessesByName("vmmem").Length > 0;
        return new Snapshot(service, startMode, wsl && ProxyListening(), wsl, ReadNics());
    }

    private static bool ProxyListening()
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(400);
            client.ConnectAsync(ProxyHost, ProxyPort, cts.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string[] ReadNics()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(ConfigPath)) is JsonObject cfg && cfg["Config"] is JsonArray nics)
                return nics.Select(n => n?["FriendlyInterfaceName"]?.GetValue<string>() ?? n?["InterfaceName"]?.GetValue<string>() ?? "?")
                    .ToArray();
        }
        catch (Exception)
        {
            // Missing or being rewritten by the service.
        }

        return Array.Empty<string>();
    }

    private void RefreshMenu()
    {
        _miService.Text = "Service: " + Describe(_last.Service) + (_last.StartMode == ServiceStartMode.Disabled ? " (disabled)" : "");
        _miWsl.Text = "WSL VM: " + (_last.Wsl ? "running" : "stopped");
        _miProxy.Text = $"Proxy {ProxyHost}:{ProxyPort}: " + (_last.Proxy ? "listening" : "not reachable");
        _miNics.Text = _last.Nics.Length == 0 ? "Interfaces: none configured yet" : "Interfaces: " + string.Join(", ", _last.Nics);

        var running = _last.Service is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        _miToggle.Text = running ? "Disable service" : "Enable service";
        _miToggle.Enabled = _last.Service != null;
        _miRestart.Enabled = running;

        RefreshModeMenu();
        _miKeepAwake.Checked = ReadTrayBool("KeepWSL2Awake", true);
        _miKernel.Checked = ReadConfigBool("ManageKernelLocation", true);
        _miEndpoints.Checked = ReadConfigBool("ManageEndpoint", true);
        _miLocalAuth.Checked = ReadConfigBool("LocalProxyRequireAuth", true);
    }

    // The mode is the "Types" flags of each interface in config.json, which the Linux side applies:
    // aggregation = every interface has "subflow"; fallback = all but the primary one also have "backup".
    private void RefreshModeMenu()
    {
        var nics = ReadNicModes();
        var backups = nics.Count(n => n.Types.Contains("backup"));
        var primary = backups > 0 && nics.Count - backups == 1 ? nics.FindIndex(n => !n.Types.Contains("backup")) : -1;
        var custom = backups > 0 && primary < 0;

        _miMode.Text = "MPTCP mode: " + (custom ? "custom" : primary >= 0 ? $"fallback ({nics[primary].Name} first)" : "aggregation");
        _miMode.Enabled = nics.Count > 0;
        _miMode.DropDownItems.Clear();

        var aggregation = Item("Aggregation: use all interfaces together", () => SetMode(-1));
        aggregation.Checked = !custom && primary < 0;
        _miMode.DropDownItems.Add(aggregation);
        for (var i = 0; i < nics.Count; i++)
        {
            var index = i;
            var fallback = Item($"Fallback: {nics[i].Name} first, the others only as backup", () => SetMode(index));
            fallback.Checked = primary == i;
            _miMode.DropDownItems.Add(fallback);
        }
    }

    private async void SetMode(int primary)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(ConfigPath)) is not JsonObject cfg || cfg["Config"] is not JsonArray nics) return;
            for (var i = 0; i < nics.Count; i++)
            {
                if (nics[i] is not JsonObject nic) continue;
                // Keep any other flag (signal, fullmesh) the user put there.
                var types = ((nic["Types"] as JsonArray)?.Select(t => t?.GetValue<string>() ?? "") ?? Enumerable.Empty<string>())
                    .Where(t => t != "" && t != "backup" && t != "subflow").ToList();
                types.Insert(0, "subflow");
                if (primary >= 0 && i != primary) types.Add("backup");
                nic["Types"] = new JsonArray(types.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
            }

            WriteJson(ConfigPath, cfg);
        }
        catch (Exception e)
        {
            MessageBox.Show("Unable to update config.json: " + e.Message, "MPTCP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var applied = await Task.Run(ApplyMode);
        _notifyIcon.ShowBalloonTip(4000, "MPTCP",
            !applied ? "Saved. WSL could not apply it now; it applies when the interfaces next get an address."
            : primary < 0 ? "Aggregation: all interfaces are used together."
            : "Fallback: the other interfaces only carry traffic if the first one fails.",
            ToolTipIcon.Info);
    }

    // Installed by install.sh; re-creates the MPTCP endpoints from config.json in the running VM.
    private static bool ApplyMode()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("wsl.exe", "-u root -- /usr/local/sbin/mptcp-apply-endpoints")
                { UseShellExecute = false, CreateNoWindow = true });
            return process != null && process.WaitForExit(30000) && process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static List<NicMode> ReadNicModes()
    {
        var result = new List<NicMode>();
        try
        {
            if (JsonNode.Parse(File.ReadAllText(ConfigPath)) is JsonObject cfg && cfg["Config"] is JsonArray nics)
                foreach (var nic in nics)
                    result.Add(new NicMode(
                        nic?["FriendlyInterfaceName"]?.GetValue<string>() ?? nic?["InterfaceName"]?.GetValue<string>() ?? "?",
                        (nic?["Types"] as JsonArray)?.Select(t => t?.GetValue<string>() ?? "").ToArray() ?? Array.Empty<string>()));
        }
        catch (Exception)
        {
            // Missing or being rewritten by the service.
        }

        return result;
    }

    private void ToggleService()
    {
        var running = _last.Service is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        // Enabling also makes it start with Windows again; disabling leaves it startable by hand.
        RunElevated(running
            ? $"sc stop {ServiceName} & sc config {ServiceName} start= demand"
            : $"sc config {ServiceName} start= auto & sc start {ServiceName}");
    }

    private void EnsureAwake()
    {
        if (!ReadTrayBool("KeepWSL2Awake", true))
        {
            StopAwake();
            return;
        }

        if (_awake is { HasExited: false }) return;
        try
        {
            // Runs in the user's own default distribution, which is the VM the service attaches the NICs to.
            _awake = Process.Start(new ProcessStartInfo("wsl.exe", "--exec sleep infinity")
                { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception)
        {
            _awake = null;
        }
    }

    private void StopAwake()
    {
        try
        {
            if (_awake is { HasExited: false }) _awake.Kill();
        }
        catch (Exception)
        {
            // Already gone.
        }

        _awake = null;
    }

    private void ExitApp()
    {
        Dispose();
        Application.Exit();
    }

    private static void RunElevated(string commandLine)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{commandLine}\"")
                { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (Win32Exception)
        {
            // The user declined the UAC prompt.
        }
    }

    private static void Run(string file, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "MPTCP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void Edit(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(path + " does not exist yet.", "MPTCP", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Run("notepad.exe", $"\"{path}\"");
    }

    private static bool ReadConfigBool(string key, bool fallback)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(ConfigPath))?[key]?.GetValue<bool>() ?? fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    // Other keys are kept as they are; the service itself rewrites this file, so write it in one move.
    private static void SaveConfigBool(string key, bool value)
    {
        try
        {
            var cfg = JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject ?? new JsonObject();
            cfg[key] = value;
            WriteJson(ConfigPath, cfg);
        }
        catch (Exception e)
        {
            MessageBox.Show("Unable to update config.json: " + e.Message, "MPTCP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // Tray-only settings live in their own file: the service would drop unknown keys from config.json.
    private static bool ReadTrayBool(string key, bool fallback)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(TrayPath))?[key]?.GetValue<bool>() ?? fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static void SaveTraySetting(string key, bool value)
    {
        try
        {
            var cfg = File.Exists(TrayPath) ? JsonNode.Parse(File.ReadAllText(TrayPath)) as JsonObject ?? new JsonObject() : new JsonObject();
            cfg[key] = value;
            Directory.CreateDirectory(DataDir);
            WriteJson(TrayPath, cfg);
        }
        catch (Exception)
        {
            // The setting just does not persist.
        }
    }

    private static void WriteJson(string path, JsonNode node)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, true);
    }

    private static string Describe(ServiceControllerStatus? status)
    {
        return status switch
        {
            null => "not installed",
            ServiceControllerStatus.Running => "running",
            ServiceControllerStatus.Stopped => "stopped",
            ServiceControllerStatus.StartPending => "starting",
            ServiceControllerStatus.StopPending => "stopping",
            var other => other.ToString().ToLowerInvariant()
        };
    }

    private static ToolStripMenuItem Label() => new() { Enabled = false };

    private static ToolStripMenuItem Item(string text, Action click)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => click();
        return item;
    }

    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("M", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }

        var handle = bmp.GetHicon();
        try
        {
            // Clone so that the native handle can be released straight away.
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private enum State
    {
        Ok,
        Degraded,
        Down
    }

    private sealed record NicMode(string Name, string[] Types);

    private sealed record Snapshot(ServiceControllerStatus? Service, ServiceStartMode? StartMode, bool Proxy, bool Wsl, string[] Nics);
}
