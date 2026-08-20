using System.Text.RegularExpressions;

namespace MaxHub.Core.Manifests;

public static class ToolCategoryClassifier
{
    private static readonly (string Name, Regex Pattern)[] Rules =
    {
        ("重命名", Pattern("rename|重命名|命名")),
        ("导入导出", Pattern(@"export|import|导出|导入|\bfbx\b|\bobj\b|\bgltf\b")),
        ("材质贴图", Pattern("material|texture|shader|材质|贴图")),
        ("UV", Pattern(@"\buv\b|unwrap|展开")),
        ("清理优化", Pattern("clean|cleanup|optimi|清理|优化|检查|check")),
        ("动画绑定", Pattern("anim|rig|bone|skin|动画|绑定|骨骼|蒙皮")),
        ("灯光渲染", Pattern("light|render|camera|灯光|渲染|相机|摄像机")),
        ("场景对象", Pattern("scene|object|selection|场景|对象|选择")),
    };

    public static IReadOnlyList<string> Categories { get; } =
        [.. Rules.Select(r => r.Name), "其他"];

    public static string Classify(string name, string? description, string? toolId = null)
    {
        var text = $"{name} {description ?? ""} {toolId ?? ""}";
        return Rules.FirstOrDefault(r => r.Pattern.IsMatch(text)).Name ?? "其他";
    }

    public static int SortOrder(string category)
    {
        for (var index = 0; index < Categories.Count; index++)
            if (Categories[index] == category)
                return index;
        return Categories.Count;
    }

    private static Regex Pattern(string expression) =>
        new(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
