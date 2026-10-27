using System.Security.Cryptography;
using DotReticulum.Crypto;

namespace DotReticulum.Crypto.Tests;

// Published test vectors: RFC 8032 section 7.1 (Ed25519), RFC 7748 section 6.1 (X25519),
// RFC 5869 appendix A cases 1-3 (HKDF-SHA256). Values were copied from the RFC texts.
[Trait("Category", "Unit")]
public class RfcVectorTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    public static TheoryData<string, string, string, string> Ed25519Vectors => new()
    {
        { "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60", "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
          "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b" },
        { "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb", "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
          "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00" },
        { "c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7", "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025", "af82",
          "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a" },
        { "833fe62409237b9d62ec77587520911e9a759cec1d19755b7da901b96dca3d42", "ec172b93ad5e563bf4932c70e1245034c35467ef2efd4d64ebf819683467e2bf",
          "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
          "dc2a4459e7369633a52b1bf277839a00201009a3efbf3ecb69bea2186c26b58909351fc9ac90b3ecfdfbc7c66431e0303dca179c138ac17ad9bef1177331a704" },
    };

    [Theory, MemberData(nameof(Ed25519Vectors))]
    public void Ed25519_Rfc8032(string sk, string pk, string msg, string sig)
    {
        Assert.Equal(H(pk), Ed25519.GetPublicKey(H(sk)));
        Assert.Equal(H(sig), Ed25519.Sign(H(sk), H(msg)));
        Assert.True(Ed25519.Verify(H(pk), H(msg), H(sig)));
    }

    [Fact]
    public void Ed25519_RejectsTamperedSignatureMessageAndBadLengths()
    {
        var v = Ed25519Vectors.First();
        var pk = H((string)v[1]);
        var sig = H((string)v[3]);
        var msg = H((string)v[2]);
        for (var i = 0; i < sig.Length; i++)
        {
            var bad = (byte[])sig.Clone();
            bad[i] ^= 1;
            Assert.False(Ed25519.Verify(pk, msg, bad), $"signature byte {i}");
        }
        Assert.False(Ed25519.Verify(pk, [1], sig));
        Assert.False(Ed25519.Verify(pk, msg, sig[..63]));
        Assert.False(Ed25519.Verify(pk, msg, [.. sig, 0]));
        Assert.False(Ed25519.Verify(pk[..31], msg, sig));
        Assert.Throws<ArgumentException>(() => Ed25519.GetPublicKey(new byte[31]));
        Assert.Throws<ArgumentException>(() => Ed25519.Sign(new byte[33], msg));
    }

    [Theory]
    [InlineData("0100000000000000000000000000000000000000000000000000000000000000")] // identity
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")] // y=0, order 4
    [InlineData("ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")] // y=-1, order 2
    [InlineData("eeffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")] // non-canonical y = 1 + p
    [InlineData("edffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")] // y=p with sign bit
    [InlineData("0200000000000000000000000000000000000000000000000000000000000000")] // y=2: not on the curve
    public void Ed25519_RejectsInvalidAndLowOrderPublicKeys(string hex)
    {
        Assert.False(Ed25519.IsValidPublicKey(H(hex)));
        // A signature can never verify under such a key.
        Assert.False(Ed25519.Verify(H(hex), [1, 2, 3], new byte[64]));
    }

    [Fact]
    public void Ed25519_AcceptsRfcPublicKeys()
    {
        foreach (var v in Ed25519Vectors) Assert.True(Ed25519.IsValidPublicKey(H((string)v[1])));
    }

    private const string AlicePrivate = "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a";
    private const string AlicePublic = "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a";
    private const string BobPrivate = "5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb";
    private const string BobPublic = "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f";
    private const string Shared = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742";

    [Fact]
    public void X25519_Rfc7748()
    {
        Assert.Equal(H(AlicePublic), X25519.GetPublicKey(H(AlicePrivate)));
        Assert.Equal(H(BobPublic), X25519.GetPublicKey(H(BobPrivate)));
        Assert.Equal(H(Shared), X25519.Agree(H(AlicePrivate), H(BobPublic)));
        Assert.Equal(H(Shared), X25519.Agree(H(BobPrivate), H(AlicePublic)));
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")] // 0
    [InlineData("0100000000000000000000000000000000000000000000000000000000000000")] // 1
    [InlineData("e0eb7a7c3b41b8ae1656e3faf19fc46ada098deb9c32b1fd866205165f49b800")] // order 8
    [InlineData("5f9c95bca3508c24b1d0b1559c83ef5b04445cc4581c8e86d8224eddd09f1157")] // order 8
    [InlineData("ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")] // p-1
    [InlineData("edffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")] // p (non-canonical 0)
    [InlineData("eeffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")] // p+1 (non-canonical 1)
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")] // >= p, high bit set
    [InlineData("0000000000000000000000000000000000000000000000000000000000000080")] // 0 with high bit
    public void X25519_RejectsLowOrderAndNonCanonicalPublicKeys(string hex)
    {
        Assert.False(X25519.IsValidPublicKey(H(hex)));
        Assert.Throws<CryptographicException>(() => X25519.Agree(H(AlicePrivate), H(hex)));
    }

    [Fact]
    public void X25519_RejectsMalformedLengths()
    {
        Assert.False(X25519.IsValidPublicKey(new byte[31]));
        Assert.False(X25519.IsValidPublicKey(new byte[33]));
        Assert.Throws<ArgumentException>(() => X25519.GetPublicKey(new byte[31]));
        Assert.Throws<ArgumentException>(() => X25519.Agree(new byte[33], H(BobPublic)));
        Assert.Throws<CryptographicException>(() => X25519.Agree(H(AlicePrivate), new byte[31]));
    }

    [Fact]
    public void X25519_GeneratedKeysAgree()
    {
        var a = X25519.GeneratePrivateKey();
        var b = X25519.GeneratePrivateKey();
        Assert.Equal(X25519.Agree(a, X25519.GetPublicKey(b)), X25519.Agree(b, X25519.GetPublicKey(a)));
    }

    [Theory]
    [InlineData("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b", "000102030405060708090a0b0c", "f0f1f2f3f4f5f6f7f8f9", 42,
        "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865")]
    [InlineData("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f404142434445464748494a4b4c4d4e4f",
        "606162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9fa0a1a2a3a4a5a6a7a8a9aaabacadaeaf",
        "b0b1b2b3b4b5b6b7b8b9babbbcbdbebfc0c1c2c3c4c5c6c7c8c9cacbcccdcecfd0d1d2d3d4d5d6d7d8d9dadbdcdddedfe0e1e2e3e4e5e6e7e8e9eaebecedeeeff0f1f2f3f4f5f6f7f8f9fafbfcfdfeff", 82,
        "b11e398dc80327a1c8e7f78c596a49344f012eda2d4efad8a050cc4c19afa97c59045a99cac7827271cb41c65e590e09da3275600c2f09b8367793a9aca3db71cc30c58179ec3e87c14c01d5c1f3434f1d87")]
    [InlineData("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b", "", "", 42,
        "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8")]
    public void Hkdf_Rfc5869(string ikm, string salt, string info, int length, string okm)
    {
        Assert.Equal(H(okm), KeyDerivation.HkdfSha256(H(ikm), length, H(salt), H(info)));
    }

    [Fact]
    public void Hkdf_EmptySaltEqualsThirtyTwoZeroBytes_AndLengthBounds()
    {
        var ikm = new byte[] { 1, 2, 3 };
        Assert.Equal(KeyDerivation.HkdfSha256(ikm, 64, new byte[32]), KeyDerivation.HkdfSha256(ikm, 64));
        Assert.Equal(8160, KeyDerivation.HkdfSha256(ikm, 8160).Length);
        Assert.Single(KeyDerivation.HkdfSha256(ikm, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyDerivation.HkdfSha256(ikm, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyDerivation.HkdfSha256(ikm, 8161));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyDerivation.HkdfSha256(ikm, -1));
        Assert.Throws<ArgumentException>(() => KeyDerivation.HkdfSha256([], 32));
    }
}
