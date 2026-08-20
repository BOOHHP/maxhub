using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MaxHub.Core.Manifests;

public static partial class ToolId
{
    [GeneratedRegex("^MaxTool[0-9]{8}$")]
    private static partial Regex CanonicalPattern();

    public static string Generate(string source)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source.Trim()));
        var number = BinaryPrimitives.ReadUInt32BigEndian(hash) % 90_000_000 + 10_000_000;
        return $"MaxTool{number:D8}";
    }

    public static string PublicCode(string id) =>
        CanonicalPattern().IsMatch(id) ? id : Generate(id);

    public static bool IsCanonical(string id) => CanonicalPattern().IsMatch(id);
}
