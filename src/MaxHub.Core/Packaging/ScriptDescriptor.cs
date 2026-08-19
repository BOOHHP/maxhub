using System.Text.RegularExpressions;

namespace MaxHub.Core.Packaging;

/// <summary>从脚本内容启发式提取名称与功能描述，供上传时预填（用户可修改确认）。</summary>
public sealed record ScriptDescriptorResult(string Name, string Description, string SuggestedId);

/// <summary>
/// 解析 MaxScript / Python 脚本：头部注释 → rollout 标题 → 文件名 → 动作关键词，逐级降级提取。
/// 提取结果仅作预填建议，不替代用户确认。
/// </summary>
public static partial class ScriptDescriptor
{
    // 常见动作词，用于从文件名/注释推断功能
    private static readonly (string Pattern, string Label)[] ActionWords =
    {
        ("rename", "重命名"), ("export", "导出"), ("import", "导入"), ("cleanup", "清理"),
        ("merge", "合并"), ("convert", "转换"), ("bake", "烘焙"), ("uv", "UV"),
        ("material", "材质"), ("texture", "贴图"), ("light", "灯光"), ("camera", "相机"),
        ("animation", "动画"), ("rig", "绑定"), ("lod", "LOD"), ("collision", "碰撞"),
        ("batch", "批量"), ("scene", "场景"), ("object", "对象"), ("selection", "选中"),
    };

    public static ScriptDescriptorResult Analyze(string fileName, string content)
    {
        var name = ExtractName(fileName, content);
        var description = ExtractDescription(content, name);
        return new ScriptDescriptorResult(name, description, SuggestId(fileName, name));
    }

    private static string ExtractName(string fileName, string content)
    {
        // 1. @name 标记（头部元数据注释）
        if (ExtractTag(content, "name") is { } tagName)
            return tagName;

        // 2. rollout 标题：rollout xxx "标题"
        var rollout = Regex.Match(content, @"rollout\s+\w+\s+""([^""]+)""", RegexOptions.IgnoreCase);
        if (rollout.Success && !string.IsNullOrWhiteSpace(rollout.Groups[1].Value))
            return rollout.Groups[1].Value.Trim();

        // 3. 文件名（去扩展名、去下划线/连字符，转可读名）
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (!string.IsNullOrWhiteSpace(baseName))
            return ToReadable(baseName);

        return "未命名工具";
    }

    /// <summary>提取 @tag 标记值（如 -- @description xxx），只取标记后到行尾的文字。</summary>
    private static string? ExtractTag(string content, string tag)
    {
        var m = Regex.Match(content, $@"@{tag}[ \t:：]+([^\r\n]+)", RegexOptions.IgnoreCase);
        var value = m.Success ? m.Groups[1].Value.Trim() : null;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string ExtractDescription(string content, string name)
    {
        // 1. @description 标记：只取标记后的文字
        if (ExtractTag(content, "description") is { } tagDesc)
            return Truncate(tagDesc, 200);

        // 2. 头部注释块：连续 -- 或 # 注释行，跳过 @tag 元数据行
        var lines = content.Split('\n');
        var header = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("--") || trimmed.StartsWith("#"))
            {
                var text = trimmed.TrimStart('-', '#', ' ', '\t');
                if (text.StartsWith('@')) continue; // @name/@category 等标记不属于描述
                if (text.Length > 0) header.Add(text);
            }
            else if (header.Count > 0)
            {
                break; // 头部注释结束
            }
        }
        if (header.Count > 0)
        {
            var joined = string.Join(" ", header).Trim();
            if (joined.Length > 0) return Truncate(joined, 200);
        }

        // 2. 动作词推断
        var actions = ActionWords
            .Where(a => content.Contains(a.Pattern, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Label)
            .Distinct()
            .ToList();
        if (actions.Count > 0)
            return $"{name}：支持{string.Join("、", actions)}。";

        return $"{name}（自动识别，请补充功能说明）";
    }

    private static string SuggestId(string fileName, string name)
    {
        // 优先用 ASCII 文件名生成 id（中文名 slug 化会为空）
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var source = !string.IsNullOrWhiteSpace(baseName) && baseName.Any(c => c < 128) ? baseName : name;
        var slug = Regex.Replace(source.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "tool";
        return $"com.company.{slug}";
    }

    private static string ToReadable(string name)
    {
        // camelCase / snake_case / kebab-case → 空格分隔
        var spaced = Regex.Replace(name, @"([a-z0-9])([A-Z])", "$1 $2");
        spaced = spaced.Replace('_', ' ').Replace('-', ' ');
        var words = spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? name : string.Join(" ", words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = s[..max];
        var lastSpace = cut.LastIndexOf(' ');
        return (lastSpace > max * 0.6 ? cut[..lastSpace] : cut) + "…";
    }
}
