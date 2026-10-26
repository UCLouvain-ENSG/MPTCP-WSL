using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MPTCP_WSL;

/// <summary>
///     Allow to create external Hyper-V switch
/// </summary>
public class HyperVManager
{
    private static readonly SemaphoreSlim Mutex = new(1);
    private readonly ILogger _logger;
    public EventHandler<CollectionUpdateEvent> OnAdd = null!;
    private readonly string vSwitchPrefix = "MPTCP - ";


    public HyperVManager(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Check if a Hyper-V switch with this exact name is already attached to the management OS
    /// </summary>
    /// <param name="switchName">The full name of the switch, prefix included</param>
    /// <returns>true if the switch exists</returns>
    private bool IsSwitchPresent(string switchName)
    {
        var result = RunPowerShell("Get-VMNetworkAdapter -ManagementOS|Select -ExpandProperty SwitchName");
        if (result.StatusCode != 0) return false;
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.Trim(), switchName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Create an external Hyper-V switch for a specific NIC
    /// </summary>
    /// <remarks>
    ///     all created Hyper-V switch begin with "MPTCP - ", only this type of naming is recognized by this app,
    ///     other naming convention will lead to complete disregard by the application and might not function as expected
    /// </remarks>
    /// <param name="netInfo">The network interface for which the switch should be created</param>
    public async Task CreateHyperVSwitch(NetworkInformation netInfo)
    {
        await Mutex.WaitAsync(); //since Hyper-V don't allow creation of switch simultaneously, a mutex is put here
        //and is released when the Task is finished
        try
        {
            await Task.Run(() =>
            {
                var interfaceConnectionId = netInfo.FriendlyInterfaceName;
                var id = vSwitchPrefix + interfaceConnectionId;
                if (!IsSwitchPresent(id))
                {
                    var command =
                        $"New-VMSwitch -Name {PowerShellQuote(id)} -NetAdapterName {PowerShellQuote(interfaceConnectionId)} -AllowManagementOS $true";
                    var result = RunPowerShell(command);
                    if (result.StatusCode != 0)
                    {
                        _logger.LogWarning("Unable to create the vSwitch : {Id} reason : {Reason}", id,
                            result.Output);
                        return;
                    }

                    _logger.LogInformation("vSwitch : {Id} created successfully", id);
                }
                else
                {
                    _logger.LogInformation("the vSwitch {Id} : already exists", id);
                }

                // Hand a copy to the listeners: the original is shared with the monitor and the saved config,
                // and must keep the physical name of the NIC.
                var switched = netInfo.Clone();
                switched.FriendlyInterfaceName = id;
                OnAdd?.Invoke(this, new CollectionUpdateEvent
                {
                    Type = EventType.Addition,
                    NetworkInfo = switched
                });
            });
        }
        finally
        {
            Mutex.Release();
        }
    }

    /// <summary>
    ///     Quote a string as a PowerShell single-quoted literal. PowerShell also treats the typographic
    ///     single quotes as quote characters, so those are doubled as well.
    /// </summary>
    private static string PowerShellQuote(string value)
    {
        var builder = new StringBuilder("'");
        foreach (var c in value)
        {
            builder.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') builder.Append(c);
        }

        return builder.Append('\'').ToString();
    }

    /// <summary>
    ///     Helper function that allow to run some PowerShell command
    /// </summary>
    /// <param name="command">the command that we want to run</param>
    /// <returns>the output of the command</returns>
    private (int StatusCode, string Output) RunPowerShell(string command)
    {
        try
        {
            // -EncodedCommand avoids any command-line quoting issue with the interface names.
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue';" + command));
            using var powerShellProcess = new Process();
            powerShellProcess.StartInfo.FileName = "powershell";
            powerShellProcess.StartInfo.Arguments =
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand {encoded}";
            powerShellProcess.StartInfo.RedirectStandardOutput = true;
            powerShellProcess.StartInfo.RedirectStandardError = true;
            powerShellProcess.StartInfo.UseShellExecute = false;
            powerShellProcess.StartInfo.CreateNoWindow = true;
            powerShellProcess.Start();

            // Read stderr concurrently, otherwise a full stderr pipe blocks the process while we wait on stdout.
            var errorsTask = powerShellProcess.StandardError.ReadToEndAsync();
            var output = powerShellProcess.StandardOutput.ReadToEnd();
            var errors = errorsTask.GetAwaiter().GetResult();

            powerShellProcess.WaitForExit();
            var statusCode = powerShellProcess.ExitCode;

            return (statusCode, output.Length != 0 ? output : errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running powershell command");
            return (-1, ex.Message);
        }
    }
}
