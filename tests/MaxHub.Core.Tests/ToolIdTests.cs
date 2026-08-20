using MaxHub.Core.Manifests;

namespace MaxHub.Core.Tests;

public class ToolIdTests
{
    [Fact]
    public void Generate_returns_stable_MaxTool_plus_eight_digits()
    {
        var first = ToolId.Generate("批量附加 Pro");
        var second = ToolId.Generate("批量附加 Pro");

        Assert.Matches("^MaxTool[0-9]{8}$", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void PublicCode_preserves_canonical_ids_and_maps_legacy_ids()
    {
        Assert.Equal("MaxTool12345678", ToolId.PublicCode("MaxTool12345678"));
        Assert.Matches("^MaxTool[0-9]{8}$", ToolId.PublicCode("com.company.pro"));
    }
}
