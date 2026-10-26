using Microsoft.Extensions.Logging;

namespace MPTCP_WSL;

/// <summary>
///     Main class of the application that allow to enable MPTCP inside WSL2
/// </summary>
public class MPTCPEnabler
{
    private readonly HyperVManager _hyperVManager;
    private readonly NetworkMonitor _networkMonitor;
    private readonly WslAttacher _wslAttacher;
    private readonly NetworkConfig _config;
    private readonly int _refreshDelay;

    /// <summary>
    ///     Create an instance of MPTCP class
    /// </summary>
    /// <param name="refreshDelay">delay in ms between each update of the network interfaces</param>
    public MPTCPEnabler(ILogger logger, CancellationToken token, int refreshDelay, NetworkConfig config)
    {
        this._refreshDelay = refreshDelay;
        _config = config;
        _hyperVManager = new HyperVManager(logger);
        _networkMonitor = new NetworkMonitor(logger, token, refreshDelay);
        _wslAttacher = new WslAttacher(logger, _config, token);
        _networkMonitor.OnUpdate += _config.NetworkMonitorOnOnUpdate;
        // async void: an exception escaping here would be unhandled and take the whole service down.
        _networkMonitor.OnUpdate += async (sender, collectionUpdateEvent) =>
        {
            if (collectionUpdateEvent.Type != EventType.Addition) return;
            try
            {
                await _hyperVManager.CreateHyperVSwitch(collectionUpdateEvent.NetworkInfo);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Unable to set up the vSwitch for {Interface}",
                    collectionUpdateEvent.NetworkInfo.FriendlyInterfaceName);
            }
        };
        _hyperVManager.OnAdd += _wslAttacher.AddEvent;
    }


    public void Start()
    {
        _networkMonitor.Loop(_refreshDelay);
    }
}
