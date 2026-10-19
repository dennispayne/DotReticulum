// One-off interop helper: prints DotReticulum-produced tokens, identity ciphertexts and signatures as JSON
// so VerifyDotNetOutput.py can open them with upstream Reticulum.
//   dotnet run tests/Interop/ExportDotNetVectors.cs > dotnet-output.json
//   PYTHONPATH=<upstream checkout> python tests/Interop/VerifyDotNetOutput.py dotnet-output.json
#:project ../../src/DotReticulum.Core/DotReticulum.Core.csproj

using System.Security.Cryptography;
using DotReticulum.Core;
using DotReticulum.Crypto;

var tokens = new List<string>();
foreach (var keySize in new[] { 32, 64 })
{
    var key = RandomNumberGenerator.GetBytes(keySize);
    using var token = new Token(key);
    foreach (var n in new[] { 0, 1, 15, 16, 17, 100, 465 })
    {
        var pt = RandomNumberGenerator.GetBytes(n);
        tokens.Add(Obj(("key", key), ("plaintext", pt), ("token", token.Encrypt(pt))));
    }
}

var identities = new List<string>();
for (var i = 0; i < 5; i++)
{
    using var id = Identity.Generate();
    var ratchetPrivate = X25519.GeneratePrivateKey();
    var ratchetPublic = X25519.GetPublicKey(ratchetPrivate);
    var message = RandomNumberGenerator.GetBytes(10 + i * 37);
    var signMessage = RandomNumberGenerator.GetBytes(33);
    identities.Add(Obj(("private_key", id.ExportPrivateKey()), ("public_key", id.ExportPublicKey()), ("hash", id.Hash), ("message", message),
        ("ciphertext", id.Encrypt(message)), ("ratchet_private", ratchetPrivate), ("ratchet_ciphertext", id.Encrypt(message, ratchetPublic)),
        ("sign_message", signMessage), ("signature", id.Sign(signMessage))));

}

Console.WriteLine($"{{\"tokens\":[{string.Join(",", tokens)}],\"identities\":[{string.Join(",", identities)}]}}");

static string Obj(params (string Name, byte[] Value)[] fields) =>
    "{" + string.Join(",", fields.Select(f => $"\"{f.Name}\":\"{Convert.ToHexStringLower(f.Value)}\"")) + "}";
