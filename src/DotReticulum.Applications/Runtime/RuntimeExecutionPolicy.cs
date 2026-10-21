namespace DotReticulum.Applications.Runtime;

public sealed record RuntimeExecutionPolicy
{
    public bool AllowDiscoveryCommands { get; init; }
    public TimeSpan DiscoveryCommandTimeout { get; init; } = TimeSpan.FromSeconds(8);
}
