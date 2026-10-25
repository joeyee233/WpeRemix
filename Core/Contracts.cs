using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WpeRemix.Core;

public enum ExportMode
{
    CompositeOnly,
    ExtractOnly,
    CompositeAndExtract
}

public sealed record ExportOptions(string OutputDirectory, ExportMode Mode);
public sealed record ExportProgress(string Stage, double Fraction);
public sealed record ExportResult(string OutputDirectory, string? ImagePath, int AssetCount, IReadOnlyList<string> Warnings);
public sealed record CompositionResult(string ImagePath, IReadOnlyList<string> Warnings);

public interface IWallpaperProcessor
{
    Task<ExportResult> ProcessAsync(string inputPath, ExportOptions options, IProgress<ExportProgress>? progress, CancellationToken cancellationToken);
}

public interface ISceneComposer
{
    Task<CompositionResult> ComposeAsync(string extractedDirectory, string destinationFile, CancellationToken cancellationToken);
}
