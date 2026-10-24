using System.IO.Ports;
using DotReticulum.Interfaces;

namespace DotReticulum.Applications.Rnsd;

public sealed partial class RnsdHost
{
    private static RNodeRadioOptions CreateRadioOptions(RnsdInterfaceConfig cfg) => new()
    {
        Frequency = cfg.GetLong("frequency", 0),
        Bandwidth = cfg.GetInt("bandwidth", 0),
        TxPower = cfg.GetInt("txpower", 0, int.MinValue),
        SpreadingFactor = cfg.GetInt("spreadingfactor", 0),
        CodingRate = cfg.GetInt("codingrate", 0),
        FlowControl = cfg.GetBool("flow_control"),
        AirtimeLimitShort = cfg.Get("airtime_limit_short") is null ? null : cfg.GetDouble("airtime_limit_short", 0, 0, 100),
        AirtimeLimitLong = cfg.Get("airtime_limit_long") is null ? null : cfg.GetDouble("airtime_limit_long", 0, 0, 100),
    };

    private static RNodeSubinterfaceOptions CreateRadioSubinterface(RnsdInterfaceConfig parent, string name, IReadOnlyDictionary<string, string> values)
    {
        var cfg = new RnsdInterfaceConfig(name, RnsdInterfaceType.RNode, false, parent.Mode, values);
        var enabled = cfg.GetBool("interface_enabled");
        if (!enabled && !parent.GetBool("enabled"))
            return new RNodeSubinterfaceOptions { Name = name, Enabled = false };
        if (cfg.Get("vport") is null) throw new FormatException($"Radio subinterface '{name}': vport is required.");
        var radio = CreateRadioOptions(cfg);
        return new RNodeSubinterfaceOptions
        {
            Name = name, Enabled = enabled, VirtualPort = cfg.GetInt("vport", 0, 0, 10),
            Outgoing = cfg.Outgoing, Frequency = radio.Frequency, Bandwidth = radio.Bandwidth, TxPower = radio.TxPower,
            SpreadingFactor = radio.SpreadingFactor, CodingRate = radio.CodingRate, FlowControl = radio.FlowControl,
            AirtimeLimitShort = radio.AirtimeLimitShort, AirtimeLimitLong = radio.AirtimeLimitLong,
        };
    }

    private static TimeSpan? RadioIdInterval(RnsdInterfaceConfig cfg) =>
        cfg.Get("id_interval") is null ? null : TimeSpan.FromSeconds(cfg.GetInt("id_interval", 0, 1));

    private static KissOptions CreateKissOptions(RnsdInterfaceConfig cfg, bool ax25 = false)
    {
        var options = ax25
            ? new Ax25KissOptions { Callsign = cfg.Get("callsign")!, Ssid = cfg.GetInt("ssid", 0, 0, 15) }
            : new KissOptions();
        return options with
        {
            BaudRate = cfg.GetInt("speed", 9600, 1),
            DataBits = cfg.GetInt("databits", 8, 5, 8),
            Parity = SerialParity(cfg),
            StopBits = cfg.GetInt("stopbits", 1, 1, 2) == 2 ? StopBits.Two : StopBits.One,
            PreambleMilliseconds = cfg.GetInt("preamble", 350, int.MinValue),
            TxTailMilliseconds = cfg.GetInt("txtail", 20, int.MinValue),
            Persistence = cfg.GetInt("persistence", 64, int.MinValue),
            SlotTimeMilliseconds = cfg.GetInt("slottime", 20, int.MinValue),
            FlowControl = cfg.GetBool("flow_control"),
            ConfiguredBitrate = cfg.ConfiguredBitrate,
            BeaconCallsign = cfg.Get("id_callsign") ?? "",
            BeaconInterval = cfg.Get("id_interval") is null ? null : TimeSpan.FromSeconds(cfg.GetInt("id_interval", 0)),
        };
    }

    private static Parity SerialParity(RnsdInterfaceConfig cfg) => cfg.Get("parity")?.ToLowerInvariant() switch
    {
        "e" or "even" => Parity.Even,
        "o" or "odd" => Parity.Odd,
        _ => Parity.None,
    };
}
