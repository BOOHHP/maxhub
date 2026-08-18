using System.Security.Cryptography;

namespace MaxHub.Core.Packaging;

/// <summary>
/// 包签名原语：对制品 zip 的 SHA-256 哈希做 ECDSA P-256 签名。
/// 服务端持私钥签名，Agent 持公钥（SPKI Base64）验签；哈希覆盖整个包内容（含 manifest）。
/// </summary>
public static class PackageSignature
{
    public static string Sign(ECDsa privateKey, string sha256Hex) =>
        Convert.ToBase64String(privateKey.SignHash(Convert.FromHexString(sha256Hex)));

    public static bool Verify(string publicKeyBase64, string sha256Hex, string? signatureBase64)
    {
        if (string.IsNullOrEmpty(signatureBase64))
            return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyHash(Convert.FromHexString(sha256Hex), Convert.FromBase64String(signatureBase64));
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
