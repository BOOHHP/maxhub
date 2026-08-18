using System.Security.Cryptography;
using MaxHub.Core.Packaging;

namespace MaxHub.Server.Storage;

/// <summary>服务端签名私钥：首次启动生成 PEM 存 dataDir/signing/，仅服务端持有。</summary>
public sealed class SigningKeyStore : IDisposable
{
    private readonly ECDsa _key;

    public string PublicKeyBase64 { get; }

    public SigningKeyStore(string dataDir)
    {
        var dir = Path.Combine(dataDir, "signing");
        Directory.CreateDirectory(dir);
        var pemPath = Path.Combine(dir, "signing-key.pem");
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(pemPath))
            _key.ImportFromPem(File.ReadAllText(pemPath));
        else
            File.WriteAllText(pemPath, _key.ExportPkcs8PrivateKeyPem());
        PublicKeyBase64 = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
    }

    public string Sign(string sha256Hex) => PackageSignature.Sign(_key, sha256Hex);

    public void Dispose() => _key.Dispose();
}
