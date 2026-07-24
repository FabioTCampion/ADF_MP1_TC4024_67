namespace PaperMachine.Historian.Infrastructure.Ads;

public sealed class AdsOptions
{
    public const string SectionName = "Ads";
    public const int PlcRuntimePort = 851;
    public const int SystemServicePort = 10_000;

    public string AmsNetId { get; set; } = string.Empty;
    public string RemoteIp { get; set; } = string.Empty;
    public int Port { get; set; } = PlcRuntimePort;
    public int OperationTimeoutMilliseconds { get; set; } = 5_000;
    public int ReconnectMinimumDelayMilliseconds { get; set; } = 1_000;
    public int ReconnectMaximumDelayMilliseconds { get; set; } = 30_000;
    public string StatusRoot { get; set; } = ".paperMachineHmiStatus";
    public string CommandsRoot { get; set; } = ".paperMachineHmiCommands";
    public string AlarmsRoot { get; set; } = ".paperMachineHmiAlarms";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AmsNetId))
            throw new InvalidOperationException("ADS AMS Net ID is required.");
        if (Port == SystemServicePort)
            throw new InvalidOperationException("TwinCAT system-service port 10000 is forbidden.");
        if (Port != PlcRuntimePort)
            throw new InvalidOperationException($"Only PLC Runtime 1 port {PlcRuntimePort} is supported.");
        if (OperationTimeoutMilliseconds is < 500 or > 60_000)
            throw new InvalidOperationException("ADS operation timeout must be between 500 and 60000 ms.");
        if (ReconnectMinimumDelayMilliseconds < 250 ||
            ReconnectMaximumDelayMilliseconds < ReconnectMinimumDelayMilliseconds)
            throw new InvalidOperationException("ADS reconnect delays are invalid.");
        if (new[] { StatusRoot, CommandsRoot, AlarmsRoot }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("All three ADS structure roots are required.");
    }
}
