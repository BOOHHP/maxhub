using MaxHub.Core.Manifests;

namespace MaxHub.Core.Tests;

public class ToolCategoryClassifierTests
{
    [Theory]
    [InlineData("批量重命名", "场景对象批处理", "重命名")]
    [InlineData("FBX Exporter", "导出选中模型", "导入导出")]
    [InlineData("材质整理", "Texture shader helper", "材质贴图")]
    [InlineData("UV Unwrap", "展开 UV", "UV")]
    [InlineData("Scene Check", "场景检查优化", "清理优化")]
    [InlineData("Skin Helper", "骨骼蒙皮绑定", "动画绑定")]
    [InlineData("Light Tool", "灯光渲染", "灯光渲染")]
    [InlineData("Object Selector", "选择场景对象", "场景对象")]
    [InlineData("Unknown", "no matching keywords", "其他")]
    public void Classify_matches_market_rules(string name, string description, string expected)
    {
        Assert.Equal(expected, ToolCategoryClassifier.Classify(name, description, "MaxTool12345678"));
    }

    [Fact]
    public void Rename_has_priority_over_scene_object()
    {
        Assert.Equal("重命名", ToolCategoryClassifier.Classify("场景对象重命名", "", "MaxTool12345678"));
    }

    [Fact]
    public void Sort_order_puts_other_last()
    {
        Assert.True(ToolCategoryClassifier.SortOrder("材质贴图") < ToolCategoryClassifier.SortOrder("其他"));
    }
}
