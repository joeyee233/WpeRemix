using System;
using System.Collections.Generic;

namespace WpeRemix.Core;

public static class CompositionNotices
{
    private static readonly HashSet<string> GeneralWarnings = new HashSet<string>(StringComparer.Ordinal)
    {
        "文字、时钟和粒子不参与静态原图合成",
        "已跳过程序生成图层（音频条、时钟合成层或纯色效果层）",
        "骨骼动画使用纹理默认姿态，裁切偏移和姿势可能与播放中的画面不同",
        "动态滤镜、光效、涟漪和脚本效果未渲染，输出为基础图层合成",
        "脚本和条件表达式使用资源包中保存的默认值，未执行脚本",
        "动画纹理仅使用第一帧",
        "动画 TEX 暂不转换；原 TEX 将保留在素材目录"
    };

    public static string Overview { get; } = string.Join(Environment.NewLine, new[]
    {
        "1. 按原尺寸合成基础图层",
        "2. 动态滤镜、光效、涟漪、脚本，以及文字、时钟、粒子和程序不参与合成",
        "3. 脚本与条件表达式使用资源包默认值",
        "4. 骨骼采用纹理默认姿态，姿势和裁切可能不同",
        "5. 动画图片仅取首帧",
        "6. 动画 TEX 不转换，原文件保留在素材目录"
    });

    public static IReadOnlyList<string> GetSpecificWarnings(IEnumerable<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        var outputs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? warning in warnings)
        {
            if (string.IsNullOrWhiteSpace(warning))
            {
                continue;
            }
            string text = warning.Trim();
            if (GeneralWarnings.Contains(text) || IsAnimatedTextureWarning(text))
            {
                continue;
            }
            if (seen.Add(text))
            {
                outputs.Add(text);
            }
        }
        return outputs;
    }

    private static bool IsAnimatedTextureWarning(string warning)
    {
        const string prefix = "跳过无效纹理 ";
        if (!warning.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        string normalized = warning.Replace('：', ':').Replace('；', ';');
        int separator = normalized.LastIndexOf(':');
        if (separator <= prefix.Length)
        {
            return false;
        }
        string reason = normalized[(separator + 1)..].Trim().Replace("; ", ";");
        return reason.Equals("动画 TEX 暂不转换;原 TEX 将保留在素材目录", StringComparison.Ordinal);
    }
}
