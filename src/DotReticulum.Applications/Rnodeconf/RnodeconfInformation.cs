using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace DotReticulum.Applications.Rnodeconf;

internal static class RnodeconfInformation
{
    internal static async Task PrintAsync(RnodeconfClient client, byte[] eeprom, string state, TextWriter output)
    {
        if (!RnodeconfClient.IsProvisioned(eeprom))
        {
            await output.WriteLineAsync("EEPROM is invalid, no further information available.").ConfigureAwait(false);
            return;
        }
        using var data = JsonDocument.Parse(RnodeconfReferenceData.Json);
        var models = data.RootElement.GetProperty("models");
        var products = data.RootElement.GetProperty("products");
        var known = models.TryGetProperty(eeprom[1].ToString("x2", CultureInfo.InvariantCulture), out var model);
        var product = products.TryGetProperty(eeprom[0].ToString("x2", CultureInfo.InvariantCulture), out var entry) ? entry.GetString() : "Unknown product";
        var signer = RnodeconfSigning.VerifyKnownSigner(eeprom, state);
        await output.WriteLineAsync($"Device info:\n  Product: {product} {(known ? model[3].GetString() : "Unknown model")} ({eeprom[0]:x2}:{eeprom[1]:x2}:{client.Board:x2})").ConfigureAwait(false);
        await output.WriteLineAsync($"  Device signature: {(signer is null ? "Unverified" : signer == "LOCAL" ? "Validated - Local signature" : "Genuine board, vendor is " + signer)}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Firmware version: {client.FirmwareVersion}\n  Hardware revision: {eeprom[2]}\n  Serial number: {Convert.ToHexStringLower(eeprom.AsSpan(3, 4))}").ConfigureAwait(false);
        if (known)
        {
            await output.WriteLineAsync($"  Modem chip: {model[5].GetString()}\n  Frequency range: {model[0].GetInt64() / 1000000.0:F3} MHz - {model[1].GetInt64() / 1000000.0:F3} MHz\n  Max TX power: {model[2].GetInt32()} dBm").ConfigureAwait(false);
        }
        var manufactured = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(eeprom.AsSpan(7, 4))).LocalDateTime;
        await output.WriteLineAsync($"  Manufactured: {manufactured:yyyy-MM-dd HH:mm:ss}").ConfigureAwait(false);
        if (eeprom[0xa7] == 0x73)
        {
            var sf = eeprom[0x9c];
            var cr = eeprom[0x9d];
            var power = eeprom[0x9e];
            var bandwidth = BinaryPrimitives.ReadUInt32BigEndian(eeprom.AsSpan(0x9f, 4));
            var frequency = BinaryPrimitives.ReadUInt32BigEndian(eeprom.AsSpan(0xa3, 4));
            var bitrate = sf > 0 && cr > 0 ? sf * (4.0 / cr) / (Math.Pow(2, sf) / bandwidth) / 1000 : 0;
            await output.WriteLineAsync($"  Device mode: TNC\n    Frequency: {frequency / 1000000.0:F3} MHz\n    Bandwidth: {bandwidth / 1000.0:F3} kHz\n    TX power: {power} dBm ({Math.Pow(10, power / 10.0):F3} mW)\n    Spreading factor: {sf}\n    Coding rate: {cr}\n    On-air bitrate: {bitrate:F3} kbps").ConfigureAwait(false);
        }
        else await output.WriteLineAsync("  Device mode: Normal (host-controlled)").ConfigureAwait(false);
    }

    internal static async Task ListModelsAsync(TextWriter output)
    {
        using var data = JsonDocument.Parse(RnodeconfReferenceData.Json);
        foreach (var model in data.RootElement.GetProperty("models").EnumerateObject())
            await output.WriteLineAsync($"{model.Name}  {model.Value[3].GetString()}  {model.Value[5].GetString()}  {model.Value[4]}").ConfigureAwait(false);
    }
}
