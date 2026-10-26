using System.Management;
using Microsoft.Extensions.Logging;

namespace MPTCP_WSL;

/// <summary>
///     Monitor Physical Network Interfaces, basically it throw an event When there is deletion or an
///     addition of a physical NIC.
/// </summary>
public class NetworkMonitor
{
    private readonly List<NetworkInformation> _interfacesNames = new();
    private readonly ILogger _logger;
    private readonly CancellationToken _token;

    /// <summary>
    ///     Initialize a new instance of the NetworkMonitor class.
    /// </summary>
    /// <param name="refreshDelay">delay in ms between each update of the network interfaces</param>
    public NetworkMonitor(ILogger logger, CancellationToken token, int refreshDelay)
    {
        _logger = logger;
        _token = token;
        //Loop(refreshDelay);
    }


    public event EventHandler<CollectionUpdateEvent> OnUpdate = null!;


    /// <summary>
    ///     Create a task that checks for addition or deletion of Physical NIC until the token is cancelled.
    /// </summary>
    /// <param name="refreshDelay">The delay in milliseconds between each update</param>
    public void Loop(int refreshDelay)
    {
        Task.Run(async () =>
        {
            while (!_token.IsCancellationRequested)
            {
                try
                {
                    UpdateNetworkInterfaces();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error while updating the network interfaces");
                }

                try
                {
                    await Task.Delay(refreshDelay, _token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, _token);
    }


    /// <summary>
    ///     Add or delete into the list names of Physical NIC's, also raise an event containing
    ///     information about which NIC have been added or deleted.
    /// </summary>
    private void UpdateNetworkInterfaces()
    {
        var current = GetAllNetworkInterfaces();
        // The query failed: keep the previous state instead of reporting every NIC as removed.
        if (current == null) return;

        foreach (var netInfo in current)
            if (!_interfacesNames.Contains(netInfo))
            {
                _interfacesNames.Add(netInfo);
                RaiseUpdate(netInfo, EventType.Addition);
            }

        foreach (var netInfo in _interfacesNames.Where(n => !current.Contains(n)).ToList())
        {
            _interfacesNames.Remove(netInfo);
            RaiseUpdate(netInfo, EventType.Deletion);
        }
    }

    /// <summary>
    ///     Invoke the handlers one by one so that a failing handler does not prevent the others from running.
    /// </summary>
    private void RaiseUpdate(NetworkInformation netInfo, EventType type)
    {
        var handlers = OnUpdate;
        if (handlers == null) return;
        var args = new CollectionUpdateEvent { NetworkInfo = netInfo, Type = type };
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<CollectionUpdateEvent>>())
            try
            {
                handler(this, args);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "A network update handler failed for {Interface}", netInfo.FriendlyInterfaceName);
            }
    }

    /// <summary>
    ///     Helper function that get all Physical NIC's.
    /// </summary>
    /// <returns>list containing all physical NIC's, or null if they could not be queried</returns>
    private List<NetworkInformation>? GetAllNetworkInterfaces()
    {
        var interfaces = new List<NetworkInformation>();
        try
        {
            var query = new ObjectQuery("SELECT * FROM Win32_NetworkAdapter " +
                                        "WHERE (PNPDeviceID LIKE 'PCI%' " +
                                        "OR PNPDeviceID LIKE 'USB%' or PNPDeviceID LIKE 'PCMCIA%') " +
                                        "AND NetConnectionID IS NOT NULL");
            using var searcher = new ManagementObjectSearcher(query);
            foreach (ManagementObject adapter in searcher.Get())
                using (adapter)
                {
                    // Adapters without a MAC address (e.g. disabled ones) cannot be bridged: skip only that one.
                    var mac = adapter["MacAddress"]?.ToString();
                    if (string.IsNullOrEmpty(mac)) continue;
                    var windowsMac = MacAddressUtil.FormatMacAddress(mac);
                    interfaces.Add(new NetworkInformation
                    {
                        WindowsMacAddress = windowsMac,
                        LinuxMacAddress = MacAddressUtil.Transform(windowsMac),
                        FriendlyInterfaceName = adapter["NetConnectionID"].ToString()!,
                        InterfaceName = adapter["Name"].ToString()!,
                        Types = new List<string>()
                    });
                }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while getting all Physical NIC's names");
            return null;
        }

        return interfaces;
    }
}
