namespace MaxHub.Agent.Core.Remote;

/// <summary>
/// 服务端签名公钥的本机固定（TOFU）：首次获取即信任并落盘，
/// 之后每次比对，不一致视为中间人或服务端换钥风险，直接拒绝。
/// </summary>
public static class TrustedKeyStore
{
    /// <summary>pin 按服务器隔离：本地开发服务器与生产服务器各自固定，互不冲突。</summary>
    public static string PinFileNameFor(string serverAuthority) =>
        $"trusted-signing-key-{serverAuthority.Replace(':', '_')}.txt";

    public static async Task<string> GetOrPinAsync(HubClient hub, string agentRoot)
    {
        var current = await hub.GetSigningPublicKeyAsync();
        var pinPath = Path.Combine(agentRoot, PinFileNameFor(hub.ServerAuthority));
        if (File.Exists(pinPath))
        {
            var pinned = File.ReadAllText(pinPath).Trim();
            if (!string.Equals(pinned, current, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "服务端签名公钥与本机固定值不一致，已拒绝操作。若确认服务端合法更换了密钥，请删除 " + pinPath + " 后重试。");
            return pinned;
        }
        Directory.CreateDirectory(agentRoot);
        File.WriteAllText(pinPath, current);
        return current;
    }
}
