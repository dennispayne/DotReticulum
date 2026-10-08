namespace DotReticulum.Crypto.Tests;

// Genuine Python RNS Identity/Token outputs, pinned to Reticulum revision
// e40191b3d193b46b7f2d8a44424a594cd758839b, using openssl / PyCA 41.0.7.
// Reproduce with generate_vectors.py and the pinned upstream checkout.
// Sequential private bytes are deliberately public test data, NEVER real keys.
internal static class PythonVectors
{
    internal static byte[] Sequence(int start, int length) =>
        Enumerable.Range(start, length).Select(x => (byte)x).ToArray();

    internal static byte[] Hex(string value) => Convert.FromHexString(value);
    internal static readonly byte[] Private = Sequence(0, 64);
    internal static readonly byte[] Public = Hex("8f40c5adb68f25624ae5b214ea767a6ec94d829d3d7b5e1ad1ba6f3e2138285f29acbae141bccaf0b22e1a94d34d0bc7361e526d0bfe12c89794bc9322966dd7");
    internal static readonly byte[] Hash = Hex("aca31af0441d81dbec71e82da0b4b5f5");
    internal static readonly byte[] Message = Hex("5265746963756c756d20696e7465726f7065726162696c6974792000ff");
    internal static readonly byte[] Signature = Hex("a35c2f6c909367366f9971f9c3a1da752fecfd47e9d37c9fbb433e5f75c0cce459a01fff7954c03310ac6b21678faa37aa06a05a9b9fd289536829dfdc82b80c");
    internal static readonly byte[] Ephemeral = Sequence(64, 32);
    internal static readonly byte[] Shared = Hex("6d54cc9c397e31691401110f58da1e182a635d7e44c21dc2d7be93624652ab15");
    internal static readonly byte[] Derived = Hex("ea96064fa8b1e58601b87877a81c63abecc53027baed51f80324e4b06f70eec97d36f013fb7f7549b0b389ab86567f9a9b1f369fc4060fbc4198eee7c8e7d7d1");
    internal static readonly byte[] Iv = Sequence(160, 16);
    internal static readonly byte[] Token256 = Hex("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf607bf26f4264f89ed29fc76cf9ae4f307cbd7d2554911ba6359e4644329e072376651dbbd87d8365a058e69dbd4f1debb835e42aae365b533556250e6d8087d4");
    internal static readonly byte[] Token128 = Hex("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf17179915ad035006c56a334b4ebf373298dfc54dd783d7da092dc0d0ed00e26b79388a60cad104f53c6e4744d1b9cb8e4703e3329a92796b571828c641682cb8");
    internal static readonly byte[] Encrypted = Hex("79a631eede1bf9c98f12032cdeadd0e7a079398fc786b88cc846ec89af85a51aa0a1a2a3a4a5a6a7a8a9aaabacadaeafbf8c4f3dc71bbdf6a48238afacb1ab9c6b74dff2ddcd1b8ab2b27ec34e284262713d4471ff4cd73818c46ffc1e1067f8b2d07e858f94f0b35fe3178cfddf74f2");
}
