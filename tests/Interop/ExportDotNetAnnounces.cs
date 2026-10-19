// One-off interop helper: prints DotReticulum-created announces (plain, app_data, ratchet, path response, tampered) and tokens
// encrypted to the announced identity/ratchet, so VerifyDotNetAnnounces.py can validate them with upstream Reticulum.
//   dotnet run tests/Interop/ExportDotNetAnnounces.cs > dotnet-announces.json
//   PYTHONPATH=<upstream checkout> python tests/Interop/VerifyDotNetAnnounces.py dotnet-announces.json
#:project ../../src/DotReticulum.Transport/DotReticulum.Transport.csproj

using System.Text.Json.Nodes;
using DotReticulum.Core;
using DotReticulum.Transport;

var cases = new JsonArray();
var engine = new AnnounceEngine();
var index = 0;
foreach (var (label, appData, useRatchet, pathResponse) in new (string, byte[], bool, bool)[]
{
    ("plain", [], false, false),
    ("app_data", "hello from dotnet"u8.ToArray(), false, false),
    ("ratchet", [], true, false),
    ("ratchet_app_data", "ratchet + app data"u8.ToArray(), true, false),
    ("path_response", "pr"u8.ToArray(), false, true),
    ("max_app_data", new byte[Packet.Mtu - Packet.Header1Size - Announce.MinPayloadLength - 32], true, false),
})
{
    using var identity = Identity.Generate();
    string[] aspects = ["vectors", $"dotnet{index++}"];
    var destination = Destination.Create(DestinationType.Single, "dotreticulum", aspects, identity);
    using var ratchets = useRatchet ? new DestinationRatchets() : null;
    var packet = Announce.Create(identity, destination, appData, ratchets, pathResponse);
    var raw = packet.ToArray();
    var tampered = (byte[])raw.Clone();
    tampered[^1] ^= 1;

    engine.Process(Packet.Parse(raw), null);
    var token = engine.Known.Encrypt(destination.Hash, "dotnet to python"u8)!;

    cases.Add(new JsonObject
    {
        ["label"] = label,
        ["app_name"] = "dotreticulum",
        ["aspects"] = new JsonArray(aspects.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
        ["identity_private"] = Convert.ToHexStringLower(identity.ExportPrivateKey()),
        ["destination_hash"] = Convert.ToHexStringLower(destination.Hash),
        ["app_data"] = Convert.ToHexStringLower(appData),
        ["ratchet_private"] = ratchets is null ? null : Convert.ToHexStringLower(ratchets.ExportPrivateKeys()[0]),
        ["raw"] = Convert.ToHexStringLower(raw),
        ["tampered"] = Convert.ToHexStringLower(tampered),
        ["token"] = Convert.ToHexStringLower(token),
    });
}

Console.WriteLine(new JsonObject { ["announces"] = cases }.ToJsonString());
