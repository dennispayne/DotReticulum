namespace DotReticulum.Core;

/// <summary>Per-operation decryption output. The caller owns the plaintext buffer.
/// RatchetId is a detached 10-byte public identifier, or null for base-key, GROUP and PLAIN decryption.
/// No mutable "last decryption" state is shared between concurrent packets.</summary>
public sealed class IdentityDecryptionResult
{
    private readonly byte[]? _ratchetId;

    internal IdentityDecryptionResult(byte[] plaintext, byte[]? ratchetId)
    {
        Plaintext = plaintext;
        _ratchetId = ratchetId;
    }

    public byte[] Plaintext { get; }
    public byte[]? RatchetId => _ratchetId?.ToArray();
}
