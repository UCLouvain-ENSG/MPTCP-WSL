using MPTCP_WSL;

namespace mptcp_enabler;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private const int RefreshDelay = 5000;
    private MPTCPEnabler _mptcpEnabler;
    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // The service starts with Windows, possibly before anybody is logged on: wait for a user profile.
            var warned = false;
            while (FileManager.GetConfigPath() == null)
            {
                if (!warned)
                {
                    _logger.LogWarning("No Windows user profile found yet (see %ProgramData%\\MPTCP\\installedOn), waiting");
                    warned = true;
                }

                await Task.Delay(RefreshDelay, stoppingToken);
            }

            NetworkConfig config = NetworkConfig.LoadConfigFromFile();
            try
            {
                new WslConfigManager(config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to update the .wslconfig file");
            }

            _mptcpEnabler = new MPTCPEnabler(_logger, stoppingToken, RefreshDelay, config);
            _mptcpEnabler.Start();

            // Stay alive until the service is stopped; the monitor runs on its own task.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // service is stopping
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "The MPTCP Enabler worker failed");
            throw;
        }
    }
}
