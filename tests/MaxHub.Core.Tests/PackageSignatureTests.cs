using System.Security.Cryptography;
using MaxHub.Core.Packaging;

namespace MaxHub.Core.Tests;

public class PackageSignatureTests
{
    [Fact]
    public void Sign_and_verify_roundtrip()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var sha256 = Convert.ToHexString(SHA256.HashData("package-bytes"u8)).ToLowerInvariant();

        var signature = PackageSignature.Sign(key, sha256);

        Assert.True(PackageSignature.Verify(publicKey, sha256, signature));
    }

    [Fact]
    public void Verify_rejects_tampered_hash_missing_or_garbage_signature_and_wrong_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var otherPublicKey = Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo());
        var sha256 = Convert.ToHexString(SHA256.HashData("package-bytes"u8)).ToLowerInvariant();
        var tampered = Convert.ToHexString(SHA256.HashData("evil-bytes"u8)).ToLowerInvariant();
        var signature = PackageSignature.Sign(key, sha256);

        Assert.False(PackageSignature.Verify(publicKey, tampered, signature));
        Assert.False(PackageSignature.Verify(publicKey, sha256, null));
        Assert.False(PackageSignature.Verify(publicKey, sha256, ""));
        Assert.False(PackageSignature.Verify(publicKey, sha256, "not-base64!!"));
        Assert.False(PackageSignature.Verify(otherPublicKey, sha256, signature));
    }
}
