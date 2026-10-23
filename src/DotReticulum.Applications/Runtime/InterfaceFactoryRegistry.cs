using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;

namespace DotReticulum.Applications.Runtime;

/// <summary>Explicitly registered, statically linked factories; no assembly discovery or runtime code execution.</summary>
public sealed class InterfaceFactoryRegistry
{
    private sealed record Registration(HashSet<string> Options, Func<RnsdInterfaceConfig, IPacketInterface> Factory);
    private readonly Dictionary<string, Registration> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private static readonly HashSet<string> CommonOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "enabled", "interface_enabled", "mode", "interface_mode",
        "bitrate", "networkname", "network_name", "passphrase", "pass_phrase", "ifac_size",
    };

    public void Register(string typeName, IEnumerable<string> supportedOptions, Func<RnsdInterfaceConfig, IPacketInterface> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(supportedOptions);
        ArgumentNullException.ThrowIfNull(factory);
        var keys = new HashSet<string>(CommonOptions, StringComparer.OrdinalIgnoreCase);
        foreach (var key in supportedOptions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            keys.Add(key);
        }
        lock (_gate)
        {
            if (!_factories.TryAdd(typeName, new Registration(keys, factory)))
                throw new ArgumentException($"An interface factory for '{typeName}' is already registered.", nameof(typeName));
        }
    }

    public bool Contains(string typeName)
    {
        lock (_gate) return _factories.ContainsKey(typeName);
    }

    public void Validate(RnsdInterfaceConfig configuration)
    {
        var registration = Find(configuration);
        foreach (var key in configuration.Options.Keys)
        {
            if (!registration.Options.Contains(key))
                throw new FormatException($"Interface '{configuration.Name}': unsupported extension setting '{key}'.");
        }
    }

    public IPacketInterface Create(RnsdInterfaceConfig configuration)
    {
        Validate(configuration);
        return Find(configuration).Factory(configuration)
            ?? throw new InvalidOperationException($"Interface factory '{configuration.Get("type")}' returned no interface.");
    }

    private Registration Find(RnsdInterfaceConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var typeName = configuration.Get("type") ?? throw new FormatException($"Interface '{configuration.Name}': missing type.");
        lock (_gate)
        {
            return _factories.TryGetValue(typeName, out var registration)
                ? registration
                : throw new FormatException($"Interface '{configuration.Name}': no statically registered factory for '{typeName}'.");
        }
    }
}
