using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WpeRemix.Core;

public sealed class WallpaperProcessor : IWallpaperProcessor
{
    private sealed record ProcessingRequest(string InputPath, ExportOptions Options, IProgress<ExportProgress>? Progress);
    private sealed record ConversionContext(string Staging, List<string> Warnings, IProgress<ExportProgress>? Progress);
    private sealed record ReportData(string Source, ExportMode Mode, IReadOnlyList<string> Warnings, string? ImagePath, string? Error);
    private static readonly object OutputLock = new();
    private readonly ISceneComposer composer;

    public WallpaperProcessor(ISceneComposer composer)
    {
        if (composer == null)
        {
            throw new ArgumentNullException(nameof(composer));
        }
        this.composer = composer;
    }

    public Task<ExportResult> ProcessAsync(string inputPath, ExportOptions options, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        var request = new ProcessingRequest(inputPath, options, progress);
        return Task.Run(() => ProcessCoreAsync(request, cancellationToken), cancellationToken);
    }

    private async Task<ExportResult> ProcessCoreAsync(ProcessingRequest request, CancellationToken cancellationToken)
    {
        string inputPath = request.InputPath;
        ExportOptions options = request.Options;
        IProgress<ExportProgress>? progress = request.Progress;
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Mode))
        {
            throw new ArgumentException("导出模式无效");
        }
        string source = Path.GetFullPath(inputPath);
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            throw new FileNotFoundException("输入文件已移动或不存在", source);
        }
        SafeFiles.RejectLinks(source);
        string outputRoot = Path.GetFullPath(options.OutputDirectory);
        string? existingParent = outputRoot;
        while (!string.IsNullOrEmpty(existingParent) && !Directory.Exists(existingParent))
        {
            existingParent = Path.GetDirectoryName(existingParent);
        }
        if (!string.IsNullOrEmpty(existingParent))
        {
            SafeFiles.RejectLinks(existingParent);
        }
        if (Directory.Exists(source))
        {
            string sourcePrefix = source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (outputRoot.Equals(source, StringComparison.OrdinalIgnoreCase) || outputRoot.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("输出目录不能位于当前输入文件夹内，请选择其他位置");
            }
        }
        Directory.CreateDirectory(outputRoot);
        string outputDirectory = ReserveDirectory(outputRoot, ReadOutputName(source));
        string workspace = Path.Combine(Path.GetTempPath(), "WpeRemix", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        SafeFiles.RejectLinks(workspace);
        var warnings = new List<string>();
        try
        {
            progress?.Report(new ExportProgress("检查并读取资源", 0.02));
            IReadOnlyList<string> sources;
            if (Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string archiveDirectory = Path.Combine(workspace, "archive");
                Directory.CreateDirectory(archiveDirectory);
                SafeFiles.ExtractZip(source, archiveDirectory, cancellationToken);
                sources = InputDiscovery.Discover(new[] { archiveDirectory });
                if (sources.Count == 0)
                {
                    throw new InvalidDataException("ZIP 内没有 PKG、TEX、场景或图片资源");
                }
                if (sources.Any(path => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException("暂不支持 ZIP 中嵌套 ZIP，请先展开内层压缩包");
                }
            }
            else
            {
                if (Directory.Exists(source) && !File.Exists(Path.Combine(source, "scene.json")))
                {
                    sources = InputDiscovery.Discover(new[] { source });
                    if (sources.Count == 0)
                    {
                        throw new InvalidDataException("文件夹内没有 PKG、TEX、场景或图片资源");
                    }
                }
                else
                {
                    sources = new[] { source };
                }
            }
            string? firstImage = null;
            int assets = 0;
            int completed = 0;
            for (int index = 0; index < sources.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string itemOutput = outputDirectory;
                if (sources.Count > 1)
                {
                    itemOutput = ReserveDirectory(outputDirectory, (index + 1).ToString("D2") + "_" + ReadOutputName(sources[index]));
                }
                try
                {
                    string staging = Path.Combine(workspace, "item_" + index);
                    Directory.CreateDirectory(staging);
                    PrepareSource(sources[index], staging, cancellationToken);
                    try
                    {
                        await ConvertTexturesAsync(new ConversionContext(staging, warnings, progress), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (options.Mode != ExportMode.CompositeOnly)
                    {
                        string recoveryAssets = Path.Combine(itemOutput, "assets");
                        SafeFiles.CopyDirectory(staging, recoveryAssets, cancellationToken);
                        throw new InvalidOperationException("纹理转换失败：" + exception.Message + Environment.NewLine + "原始素材已保存至：" + recoveryAssets, exception);
                    }
                    assets += SafeFiles.EnumerateFiles(staging, cancellationToken).Count(SafeFiles.IsImage);
                    string? imagePath = null;
                    string? savedAssets = null;
                    if (options.Mode != ExportMode.CompositeOnly)
                    {
                        progress?.Report(new ExportProgress("保存原始及转换后的素材", 0.62));
                        savedAssets = Path.Combine(itemOutput, "assets");
                        SafeFiles.CopyDirectory(staging, savedAssets, cancellationToken);
                    }
                    if (options.Mode != ExportMode.ExtractOnly)
                    {
                        progress?.Report(new ExportProgress("按原尺寸合成静态 PNG", 0.65));
                        CompositionResult composition;
                        try
                        {
                            composition = await composer.ComposeAsync(staging, Path.Combine(itemOutput, "wallpaper.png"), cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception) when (savedAssets != null)
                        {
                            throw new InvalidOperationException("静态合成失败：" + exception.Message + Environment.NewLine + "素材已保存至：" + savedAssets, exception);
                        }
                        imagePath = composition.ImagePath;
                        warnings.AddRange(composition.Warnings);
                        if (firstImage == null)
                        {
                            firstImage = imagePath;
                        }
                    }
                    WriteReport(itemOutput, new ReportData(source, options.Mode, warnings, imagePath, null));
                    completed++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (sources.Count > 1)
                {
                    warnings.Add("子项目处理失败：" + Path.GetFileName(sources[index]) + "；" + exception.Message);
                    WriteReport(itemOutput, new ReportData(source, options.Mode, warnings, null, exception.Message));
                }
                progress?.Report(new ExportProgress("已完成 " + (index + 1) + " / " + sources.Count, Math.Min(0.98, 0.1 + 0.85 * (index + 1) / sources.Count)));
            }
            if (completed == 0)
            {
                throw new InvalidDataException("压缩包中的项目均未成功处理，请查看 report.txt");
            }
            if (sources.Count > 1)
            {
                warnings.Add("此输入包含 " + sources.Count + " 个项目，已分别保存至子文件夹，预览显示首张成功图片");
            }
            WriteReport(outputDirectory, new ReportData(source, options.Mode, warnings.Distinct().ToList(), firstImage, null));
            progress?.Report(new ExportProgress("完成", 1.0));
            return new ExportResult(outputDirectory, firstImage, assets, warnings.Distinct().ToArray());
        }
        catch (OperationCanceledException)
        {
            WriteReport(outputDirectory, new ReportData(source, options.Mode, warnings, null, "用户已取消，原始输入未修改，已写出的完整结果保留"));
            throw;
        }
        catch (Exception exception)
        {
            WriteReport(outputDirectory, new ReportData(source, options.Mode, warnings, null, exception.Message));
            throw new InvalidOperationException("处理失败：" + exception.Message + Environment.NewLine + "报告：" + Path.Combine(outputDirectory, "report.txt"), exception);
        }
        finally
        {
            SafeFiles.DeleteTemporaryDirectory(workspace);
        }
    }

    private static void PrepareSource(string source, string staging, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(source))
        {
            SafeFiles.CopyDirectory(source, staging, cancellationToken);
            return;
        }
        string extension = Path.GetExtension(source);
        if (extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase))
        {
            SafeFiles.ExtractPackage(source, staging, cancellationToken);
            string project = Path.Combine(Path.GetDirectoryName(source)!, "project.json");
            if (File.Exists(project) && !File.Exists(Path.Combine(staging, "project.json")))
            {
                SafeFiles.RejectLinks(project);
                if (new FileInfo(project).Length > 4 * 1024 * 1024)
                {
                    throw new InvalidDataException("project.json 超过 4 MB 限制");
                }
                File.Copy(project, Path.Combine(staging, "project.json"));
            }
            return;
        }
        if (extension.Equals(".tex", StringComparison.OrdinalIgnoreCase) || SafeFiles.IsImage(source))
        {
            if (new FileInfo(source).Length > SafeFiles.MaximumFileBytes)
            {
                throw new InvalidDataException("输入文件超过 512 MB 限制");
            }
            File.Copy(source, Path.Combine(staging, Path.GetFileName(source)));
            return;
        }
        throw new InvalidDataException("不支持此文件类型，请选择 PKG、TEX、ZIP、壁纸文件夹或图片");
    }

    private static async Task ConvertTexturesAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        string staging = context.Staging;
        List<string> warnings = context.Warnings;
        IProgress<ExportProgress>? progress = context.Progress;
        string[] textures = SafeFiles.EnumerateFiles(staging, cancellationToken).Where(path => Path.GetExtension(path).Equals(".tex", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (textures.Length == 0)
        {
            return;
        }
        string executable = Path.Combine(AppContext.BaseDirectory, "Tools", "RePKG.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("缺少 Tools/RePKG.exe，请使用完整的 WpeRemix 程序包", executable);
        }
        for (int index = 0; index < textures.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string texture = textures[index];
            progress?.Report(new ExportProgress("转换纹理 " + (index + 1) + " / " + textures.Length, 0.12 + 0.45 * index / textures.Length));
            bool existingImage = HasConvertedSibling(texture);
            if (existingImage)
            {
                warnings.Add("纹理存在同名图片或视频，优先使用现有素材且未覆盖：" + Path.GetRelativePath(staging, texture));
                continue;
            }
            try
            {
                TexturePreflight.Validate(texture);
                var processStart = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(executable)!
                };
                processStart.ArgumentList.Add("extract");
                processStart.ArgumentList.Add("-o");
                processStart.ArgumentList.Add(Path.GetDirectoryName(texture)!);
                processStart.ArgumentList.Add(texture);
                using var process = new Process { StartInfo = processStart };
                process.Start();
                using var processTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                processTimeout.CancelAfter(TimeSpan.FromMinutes(2));
                using CancellationTokenRegistration registration = processTimeout.Token.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(true);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                });
                Task<string> stdout = ReadBoundedAsync(process.StandardOutput);
                Task<string> stderr = ReadBoundedAsync(process.StandardError);
                await process.WaitForExitAsync(processTimeout.Token).ConfigureAwait(false);
                string log = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
                string basePath = Path.Combine(Path.GetDirectoryName(texture)!, Path.GetFileNameWithoutExtension(texture));
                bool converted = HasConvertedSibling(texture);
                if (process.ExitCode != 0 || !converted)
                {
                    warnings.Add("纹理未能转换，原 TEX 保留在素材目录：" + Path.GetRelativePath(staging, texture));
                    await File.WriteAllTextAsync(basePath + ".conversion-log.txt", log, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                warnings.Add("纹理转换超过 2 分钟，已跳过：" + Path.GetRelativePath(staging, texture));
            }
            catch (InvalidDataException exception)
            {
                warnings.Add("跳过无效纹理 " + Path.GetRelativePath(staging, texture) + "：" + exception.Message);
            }
        }
    }

    private static bool HasConvertedSibling(string texture)
    {
        return Directory.EnumerateFiles(Path.GetDirectoryName(texture)!).Any(path => Path.GetFileNameWithoutExtension(path).Equals(Path.GetFileNameWithoutExtension(texture), StringComparison.OrdinalIgnoreCase) && (SafeFiles.IsImage(path) || Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var content = new StringBuilder();
        char[] buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (content.Length < 32768)
            {
                content.Append(buffer, 0, Math.Min(read, 32768 - content.Length));
            }
        }
        return content.ToString();
    }

    private static string ReserveDirectory(string root, string name)
    {
        lock (OutputLock)
        {
            string safeName = SafeFiles.SafeName(name);
            string result = Path.Combine(root, safeName);
            int suffix = 2;
            while (true)
            {
                if (!Directory.Exists(result) && !File.Exists(result))
                {
                    Directory.CreateDirectory(result);
                    try
                    {
                        string ownershipMarker = Path.Combine(result, ".wperip-output");
                        using var ownershipFile = new FileStream(ownershipMarker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        File.SetAttributes(ownershipMarker, FileAttributes.Hidden);
                        return result;
                    }
                    catch (IOException)
                    {
                        // Another WpeRemix instance reserved this destination first.
                    }
                }
                result = Path.Combine(root, safeName + " (" + suffix + ")");
                suffix++;
            }
        }
    }

    private static string ReadOutputName(string source)
    {
        string basename = "Wallpaper";
        string? sourceName = Path.GetFileNameWithoutExtension(source.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            basename = sourceName;
        }
        if (Path.GetFileName(source).Equals("scene.pkg", StringComparison.OrdinalIgnoreCase))
        {
            string? parentName = Path.GetFileName(Path.GetDirectoryName(source));
            if (!string.IsNullOrWhiteSpace(parentName))
            {
                basename = parentName;
            }
        }
        string parent = source;
        if (File.Exists(source))
        {
            parent = Path.GetDirectoryName(source)!;
        }
        string project = Path.Combine(parent, "project.json");
        if (File.Exists(project) && new FileInfo(project).Length <= 4 * 1024 * 1024)
        {
            try
            {
                SafeFiles.RejectLinks(project);
                using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(project));
                if (metadata.RootElement.TryGetProperty("title", out JsonElement title) && title.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(title.GetString()))
                {
                    basename = title.GetString() + "_" + basename;
                }
            }
            catch (JsonException)
            {
            }
        }
        return SafeFiles.SafeName(basename);
    }

    private static void WriteReport(string directory, ReportData data)
    {
        string source = data.Source;
        ExportMode mode = data.Mode;
        IReadOnlyList<string> warnings = data.Warnings;
        string? imagePath = data.ImagePath;
        string? error = data.Error;
        var report = new StringBuilder();
        report.AppendLine("WpeRemix · 处理报告");
        report.AppendLine("时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        report.AppendLine("输入：" + source);
        report.AppendLine("模式：" + mode);
        if (imagePath != null)
        {
            report.AppendLine("PNG：" + imagePath);
        }
        string assetsDirectory = Path.Combine(directory, "assets");
        if (Directory.Exists(assetsDirectory))
        {
            report.AppendLine("素材：" + assetsDirectory);
        }
        report.AppendLine("原始输入文件保持不变；静态合成不等同于 Wallpaper Engine 实时画面");
        if (error != null)
        {
            report.AppendLine("状态：未完成");
            report.AppendLine("原因：" + error);
            report.AppendLine("如需保留包中素材，可查看已提取的场景素材后重试");
        }
        else
        {
            report.AppendLine("状态：完成");
        }
        if (warnings.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("注意事项：");
            foreach (string warning in warnings.Distinct())
            {
                report.AppendLine("- " + warning);
            }
        }
        File.WriteAllText(Path.Combine(directory, "report.txt"), report.ToString(), Encoding.UTF8);
    }
}
