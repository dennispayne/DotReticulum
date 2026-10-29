using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using DotReticulum.Interfaces;

namespace DotReticulum.Applications.Rnodeconf;

/// <summary>Exclusive, bounded maintenance session. Does not start a radio or send packet data.</summary>
public sealed class RnodeconfClient(Stream stream, TimeSpan? responseTimeout = null) : IDisposable
{
    private readonly RNodeFrameDecoder _decoder = new();
    private readonly Dictionary<byte, byte[]> _responses = [];
    private readonly TimeSpan _timeout = responseTimeout ?? TimeSpan.FromSeconds(3);
    public byte Platform { get; private set; }
    public byte Board => _responses[0x47][0];
    public byte Mcu => _responses[0x49][0];
    public string FirmwareVersion => $"{_responses[0x50][0]}.{_responses[0x50][1]:D2}";

    public async Task<byte[]> DeviceHashAsync(CancellationToken token)
    {
        await ReadUntilAsync(() => Has(0x56, 32), token).ConfigureAwait(false);
        return _responses[0x56].ToArray();
    }

    public async Task<byte[]> FirmwareHashAsync(bool target, CancellationToken token)
    {
        var type = (byte)(target ? 1 : 2);
        if (!_hashes.ContainsKey(type)) await ReadUntilAsync(() => _hashes.ContainsKey(type), token).ConfigureAwait(false);
        return _hashes[type].ToArray();
    }
    public async Task<byte[]> RefreshFirmwareHashAsync(bool target, CancellationToken token)
    {
        var type = (byte)(target ? 1 : 2);
        _hashes.Remove(type);
        await stream.WriteAsync(RNodeProtocol.Frame(0x60, [type]), token).ConfigureAwait(false);
        return await FirmwareHashAsync(target, token).ConfigureAwait(false);
    }
    public async Task<uint> BluetoothPinAsync(CancellationToken token)
    {
        await ReadUntilAsync(() => Has(0x62, 4), token).ConfigureAwait(false);
        var pin = BinaryPrimitives.ReadUInt32BigEndian(_responses[0x62]);
        if (pin > 999999) throw new IOException("Device returned an invalid Bluetooth PIN.");
        return pin;
    }
    private readonly Dictionary<byte, byte[]> _hashes = [];

    public async Task IdentifyAsync(TextWriter output, CancellationToken token)
    {
        await stream.WriteAsync(RnodeconfCommands.Detect(), token).ConfigureAwait(false);
        await ReadUntilAsync(() => Has(8, 1) && Has(0x50, 2) && Has(0x48, 1) && Has(0x49, 1) && Has(0x47, 1), token).ConfigureAwait(false);
        if (_responses[8][0] != 0x46) throw new IOException("Device did not identify as an RNode.");
        Platform = _responses[0x48][0];
        await output.WriteLineAsync($"RNode firmware {_responses[0x50][0]}.{_responses[0x50][1]}, platform 0x{Platform:x2}, MCU 0x{_responses[0x49][0]:x2}, board 0x{_responses[0x47][0]:x2}").ConfigureAwait(false);
        await output.WriteLineAsync("Serial identification is not authentication; EEPROM signature verification is reported separately.").ConfigureAwait(false);
    }

    public async Task<byte[]> ReadEepromAsync(CancellationToken token)
    {
        _responses.Remove(0x51);
        await stream.WriteAsync(RnodeconfCommands.Encode("eeprom-read"), token).ConfigureAwait(false);
        await ReadUntilAsync(() => Has(0x51, 188), token).ConfigureAwait(false);
        return _responses[0x51].ToArray();
    }

    public static bool IsProvisioned(ReadOnlySpan<byte> eeprom) =>
        eeprom.Length >= 188 && eeprom[0x9b] == 0x73 &&
        CryptographicOperations.FixedTimeEquals(MD5.HashData(eeprom[..11]), eeprom.Slice(11, 16));

    public async Task PrintConfigurationAsync(byte[] eeprom, bool showPsk, TextWriter output, CancellationToken token)
    {
        _responses.Remove(0x6d);
        await stream.WriteAsync(RnodeconfCommands.Encode("config-read"), token).ConfigureAwait(false);
        await ReadUntilAsync(() => _responses.ContainsKey(0x6d), token).ConfigureAwait(false);
        var sector = _responses[0x6d];
        if (sector.Length is > 0 and < 74) throw new IOException("Device returned a truncated WiFi configuration sector.");
        string Text(int offset)
        {
            var bytes = sector.AsSpan(offset, 32);
            var end = bytes.IndexOfAny((byte)0, (byte)255);
            return new UTF8Encoding(false, true).GetString(end < 0 ? bytes : bytes[..end]);
        }
        await output.WriteLineAsync("Device configuration:").ConfigureAwait(false);
        await output.WriteLineAsync($"  Bluetooth: {(eeprom[0xb0] == 0x73 ? "Enabled" : "Disabled")}").ConfigureAwait(false);
        await output.WriteLineAsync($"  WiFi: {eeprom[0xba] switch { 1 => "STATION", 2 => "AP", _ => "OFF" }}").ConfigureAwait(false);
        if (sector.Length == 0)
            await output.WriteLineAsync("  WiFi configuration sector: not provided by this device.").ConfigureAwait(false);
        else
        {
            var psk = Text(33);
            await output.WriteLineAsync($"  Channel: {(eeprom[0xbb] is >= 1 and <= 14 ? eeprom[0xbb] : 1)}").ConfigureAwait(false);
            await output.WriteLineAsync($"  SSID: {Text(0)}").ConfigureAwait(false);
            await output.WriteLineAsync($"  PSK: {(showPsk ? psk : new string('*', psk.Length))}").ConfigureAwait(false);
            var ip = new System.Net.IPAddress(sector.AsSpan(66, 4)).ToString();
            var netmask = new System.Net.IPAddress(sector.AsSpan(70, 4)).ToString();
            if (eeprom[0xba] == 2) { ip = "10.0.0.1"; netmask = "255.255.255.0"; }
            await output.WriteLineAsync($"  IP Address: {(ip is "0.0.0.0" or "255.255.255.255" ? "DHCP" : ip)}").ConfigureAwait(false);
            await output.WriteLineAsync($"  Network Mask: {netmask}").ConfigureAwait(false);
        }
        await output.WriteLineAsync($"  Display brightness: {eeprom[0xb2]}, timeout: {eeprom[0xb4]}, rotation: {eeprom[0xb8]}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Display address: {(eeprom[0xb3] == 255 ? "Default" : eeprom[0xb3].ToString("x2", System.Globalization.CultureInfo.InvariantCulture))}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Display blanking: {(eeprom[0xb7] == 0x73 && eeprom[0xb4] != 0 ? eeprom[0xb4] + "s" : "Disabled")}").ConfigureAwait(false);
        await output.WriteLineAsync("  Display rotation: " + (eeprom[0xb8] switch { 0 => "Landscape", 1 => "Portrait", 2 => "Landscape 180", 3 => "Portrait 180", _ => "Default" })).ConfigureAwait(false);
        if (eeprom[0xb5] == 0x73) await output.WriteLineAsync($"  NeoPixel intensity: {eeprom[0xb6]}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Interference avoidance: {(eeprom[0xb9] == 0 ? "Enabled" : "Disabled")}").ConfigureAwait(false);
    }

    public async Task WriteAuthorizedAsync(IEnumerable<byte[]> commands, bool authorized, CancellationToken token)
    {
        if (!authorized) throw new UnauthorizedAccessException("Device writes require --authorize-device-write.");
        foreach (var command in commands) await stream.WriteAsync(command, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private bool Has(byte command, int size) => _responses.TryGetValue(command, out var data) && data.Length >= size;

    public void Dispose()
    {
        foreach (var data in _responses.Values) CryptographicOperations.ZeroMemory(data);
        foreach (var data in _hashes.Values) CryptographicOperations.ZeroMemory(data);
        _responses.Clear();
    }

    private async Task ReadUntilAsync(Func<bool> complete, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_timeout);
        var buffer = new byte[1024];
        var total = 0;
        try
        {
            while (!complete())
            {
                var count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("RNode disconnected before its response.");
                total += count;
                if (total > 65536) throw new IOException("RNode response budget exceeded.");
                foreach (var frame in _decoder.Feed(buffer.AsSpan(0, count)))
                {
                    if (frame.Command == 0x90) throw new IOException($"RNode hardware error 0x{frame.Payload[0]:x2}.");
                    if (frame.Command == 0x60 && frame.Payload.Length == 33 && frame.Payload[0] is 1 or 2)
                        _hashes[frame.Payload[0]] = frame.Payload.AsSpan(1).ToArray();
                    _responses[frame.Command] = frame.Payload;
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for the RNode response.");
        }
    }
}
