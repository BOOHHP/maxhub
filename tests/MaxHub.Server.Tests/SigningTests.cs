using System.Net.Http.Json;
using System.Text.Json;
using MaxHub.Core.Packaging;

namespace MaxHub.Server.Tests;

/// <summary>发布制品必须携带可用服务端公钥验证的 ECDSA 签名。</summary>
public class SigningTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Published_release_carries_signature_verifiable_with_server_public_key()
    {
        var anonymous = fixture.CreateClient();
        var keyInfo = await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/signing/public-key");
        var publicKey = keyInfo.GetProperty("publicKey").GetString()!;
        Assert.False(string.IsNullOrEmpty(publicKey));

        var api = new ApiTests(fixture);
        await api.PublishAndApprovePublicAsync("scene-batch-renamer");

        var viewer = await api.LoginPublicAsync("emp-signing-viewer", "验签用户");
        var plan = await viewer.GetFromJsonAsync<JsonElement>(
            "/api/v1/tools/com.company.scene-batch-renamer/releases/1.4.0/install-plan");
        var sha256 = plan.GetProperty("sha256").GetString()!;
        var signature = plan.GetProperty("signature").GetString();

        Assert.True(PackageSignature.Verify(publicKey, sha256, signature));
        // 签名与哈希绑定：换一个哈希即失败
        var otherHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("x"u8)).ToLowerInvariant();
        Assert.False(PackageSignature.Verify(publicKey, otherHash, signature));
    }
}
