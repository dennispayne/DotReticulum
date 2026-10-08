using System.Security.Cryptography;
using static DotReticulum.Crypto.Tests.PythonVectors;

namespace DotReticulum.Crypto.Tests;

public class PrimitiveTests
{
    // RFC 8032 section 7.1 test vectors 1 and 2.
    [Theory]
    [InlineData("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
    [InlineData("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
        "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
    public void Ed25519MatchesRfc8032(string seed, string publicKey, string message, string signature)
    {
        Assert.Equal(Hex(publicKey), Ed25519.GetPublicKey(Hex(seed)));
        Assert.Equal(Hex(signature), Ed25519.Sign(Hex(seed), Hex(message)));
        Assert.True(Ed25519.Verify(Hex(publicKey), Hex(message), Hex(signature)));
        byte[] altered = Hex(signature);
        altered[0] ^= 1;
        Assert.False(Ed25519.Verify(Hex(publicKey), Hex(message), altered));
    }

    [Fact]
    public void X25519MatchesRfc7748Section61()
    {
        byte[] alice = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        byte[] bob = Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        byte[] alicePublic = Hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
        byte[] bobPublic = Hex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
        byte[] shared = Hex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");
        Assert.Equal(alicePublic, X25519.GetPublicKey(alice));
        Assert.Equal(bobPublic, X25519.GetPublicKey(bob));
        Assert.Equal(shared, X25519.Agree(alice, bobPublic));
        Assert.Equal(shared, X25519.Agree(bob, alicePublic));
    }

    [Fact]
    public void HkdfMatchesRfc5869AppendixA1AndA3()
    {
        byte[] ikm = Enumerable.Repeat((byte)0x0b, 22).ToArray();
        Assert.Equal(Hex("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"),
            KeyDerivation.HkdfSha256(ikm, 42, Sequence(0, 13), Sequence(240, 10)));
        Assert.Equal(Hex("8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8"),
            KeyDerivation.HkdfSha256(ikm, 42));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyDerivation.HkdfSha256(ikm, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyDerivation.HkdfSha256(ikm, 8161));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void RejectsMalformedRawKeys(int length)
    {
        byte[] key = new byte[length];
        Assert.Throws<ArgumentException>(() => Ed25519.GetPublicKey(key));
        Assert.Throws<ArgumentException>(() => Ed25519.Sign(key, Message));
        Assert.Throws<ArgumentException>(() => X25519.GetPublicKey(key));
        Assert.Throws<ArgumentException>(() => X25519.Agree(key, Public.AsSpan(0, 32)));
        Assert.Throws<ArgumentException>(() => X25519.Agree(Private.AsSpan(0, 32), key));
        Assert.False(Ed25519.IsValidPublicKey(key));
        Assert.False(X25519.IsValidPublicKey(key));
        Assert.False(Ed25519.Verify(key, Message, Signature));
    }

    // RFC 7748's zero-agreement check must catch all low-order encodings,
    // including noncanonical aliases accepted by its input decoding.
    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("0100000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("e0eb7a7c3b41b8ae1656e3faf19fc46ada098deb9c32b1fd866205165f49b800")]
    [InlineData("5f9c95bca3508c24b1d0b1559c83ef5b04445cc4581c8e86d8224eddd09f1157")]
    [InlineData("ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")]
    [InlineData("edffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")]
    [InlineData("eeffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")]
    public void RejectsLowOrderX25519(string publicKey)
    {
        Assert.False(X25519.IsValidPublicKey(Hex(publicKey)));
        Assert.Throws<CryptographicException>(() => X25519.Agree(Private.AsSpan(0, 32), Hex(publicKey)));
    }

    [Fact]
    public void RejectsMalformedSignaturesAndLowOrderEd25519()
    {
        byte[] neutralPoint = new byte[32];
        neutralPoint[0] = 1;
        Assert.False(Ed25519.IsValidPublicKey(neutralPoint));
        Assert.False(Ed25519.IsValidPublicKey(new byte[32]));
        Assert.False(Ed25519.IsValidPublicKey(Enumerable.Repeat((byte)255, 32).ToArray()));
        foreach (int length in new[] { 0, 63, 65 })
            Assert.False(Ed25519.Verify(Public.AsSpan(32), Message, new byte[length]));
        Assert.False(Ed25519.Verify(Public.AsSpan(32), new byte[0], Signature));
        Assert.Equal(32, Ed25519.GeneratePrivateKey().Length);
        Assert.Equal(32, X25519.GeneratePrivateKey().Length);
    }
}
