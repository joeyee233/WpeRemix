using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace WpeRemix.Core;

internal static class SafeFiles
{
    private sealed record CopyOperation(Stream Input, Stream Output, long Length);
    internal const long MaximumInputBytes = 2L * 1024 * 1024 * 1024;
    internal const long MaximumExtractedBytes = 4L * 1024 * 1024 * 1024;
    internal const long MaximumFileBytes = 512L * 1024 * 1024;
    internal const int MaximumEntries = 20000;
    internal static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    internal static bool IsImage(string path)
    {
        return ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    internal static void RejectLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("暂不支持符号链接或目录联接，请选择实际文件夹");
            }
            current = Path.GetDirectoryName(current);
        }
    }

    internal static string ResolveEntry(string root, string entryName)
    {
        string normalized = entryName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) || normalized.Length > 512)
        {
            throw new InvalidDataException("资源包包含无效路径");
        }
        string[] segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            if (segment == "." || segment == ".." || segment.EndsWith(' ') || segment.EndsWith('.') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException("资源包包含不安全的文件路径：" + entryName);
            }
            string stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]))
            {
                throw new InvalidDataException("资源包包含 Windows 保留名称：" + entryName);
            }
        }
        string rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string destination = Path.GetFullPath(Path.Combine(rootPath, normalized));
        if (!destination.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("资源包文件超出目标目录");
        }
        return destination;
    }

    internal static IReadOnlyList<string> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        RejectLinks(root);
        var results = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        int entryCount = 0;
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (directory.Depth > 32)
            {
                throw new InvalidDataException("文件夹层级过深，最多支持 32 层");
            }
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entryCount++;
                if (entryCount > MaximumEntries)
                {
                    throw new InvalidDataException("文件和目录数量超过 20,000 个，请选择更小的壁纸目录");
                }
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("输入目录包含符号链接或目录联接：" + entry);
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push((entry, directory.Depth + 1));
                }
                else
                {
                    results.Add(entry);
                }
            }
        }
        return results;
    }

    internal static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        var files = EnumerateFiles(source, cancellationToken);
        long total = 0;
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long length = new FileInfo(file).Length;
            total = checked(total + length);
            if (length > MaximumFileBytes || total > MaximumExtractedBytes)
            {
                throw new InvalidDataException("素材超过大小限制（单文件 512 MB，总量 4 GB）");
            }
            string output = ResolveEntry(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            string partialPath = output + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var target = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    CopyBytes(new CopyOperation(input, target, length), cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(partialPath, output, false);
            }
            finally
            {
                if (File.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }
            }
        }
    }

    internal static void ExtractZip(string source, string destination, CancellationToken cancellationToken)
    {
        if (new FileInfo(source).Length > MaximumInputBytes)
        {
            throw new InvalidDataException("压缩包超过 2 GB 限制");
        }
        using var archive = ZipFile.OpenRead(source);
        if (archive.Entries.Count > MaximumEntries)
        {
            throw new InvalidDataException("ZIP 文件数量超过 20,000 个");
        }
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string output = ResolveEntry(destination, entry.FullName);
            if (!paths.Add(output))
            {
                throw new InvalidDataException("ZIP 包含重复或大小写冲突的路径");
            }
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("ZIP 包含不支持的符号链接");
            }
            total = checked(total + entry.Length);
            if (entry.Length > MaximumFileBytes || total > MaximumExtractedBytes)
            {
                throw new InvalidDataException("ZIP 展开后超过大小限制（单文件 512 MB，总量 4 GB）");
            }
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(output);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using Stream input = entry.Open();
            using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyBytes(new CopyOperation(input, target, entry.Length), cancellationToken);
        }
    }

    internal static void ExtractPackage(string source, string destination, CancellationToken cancellationToken)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumInputBytes)
        {
            throw new InvalidDataException("PKG 文件超过 2 GB 限制");
        }
        using var reader = new BinaryReader(input, new UTF8Encoding(false, true), true);
        string magic = ReadSizedString(reader, 32);
        if (!magic.StartsWith("PKGV", StringComparison.Ordinal) || magic.Length != 8 || !magic.Skip(4).All(char.IsAsciiDigit) || !int.TryParse(magic.AsSpan(4), out int version) || version < 1)
        {
            throw new InvalidDataException("无法识别 PKG 文件头，仅支持 Wallpaper Engine 的 PKGV 资源容器");
        }
        int count = reader.ReadInt32();
        if (count is < 1 or > MaximumEntries)
        {
            throw new InvalidDataException("PKG 文件条目数量无效");
        }
        var entries = new List<(string Output, long Offset, int Length)>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string output = ResolveEntry(destination, ReadSizedString(reader, 512));
            int offset = reader.ReadInt32();
            int length = reader.ReadInt32();
            total = checked(total + length);
            if (offset < 0 || length < 0 || length > MaximumFileBytes || total > MaximumExtractedBytes)
            {
                throw new InvalidDataException("PKG 条目的偏移或长度无效");
            }
            if (!paths.Add(output))
            {
                throw new InvalidDataException("PKG 包含重复或大小写冲突的路径");
            }
            entries.Add((output, offset, length));
        }
        long dataStart = input.Position;
        foreach (var entry in entries)
        {
            if (entry.Offset > input.Length - dataStart || entry.Length > input.Length - dataStart - entry.Offset)
            {
                throw new InvalidDataException("PKG 文件不完整，素材内容已截断");
            }
        }
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(entry.Output)!);
            input.Position = dataStart + entry.Offset;
            using var target = new FileStream(entry.Output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyBytes(new CopyOperation(input, target, entry.Length), cancellationToken);
        }
    }

    private static string ReadSizedString(BinaryReader reader, int limit)
    {
        int length = reader.ReadInt32();
        if (length < 1 || length > limit || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("PKG 字符串长度无效");
        }
        byte[] bytes = reader.ReadBytes(length);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static void CopyBytes(CopyOperation operation, CancellationToken cancellationToken)
    {
        Stream input = operation.Input;
        Stream output = operation.Output;
        long length = operation.Length;
        byte[] buffer = new byte[81920];
        long remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = input.Read(buffer, 0, requested);
            if (read == 0)
            {
                throw new InvalidDataException("文件内容已截断");
            }
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    internal static string SafeName(string name)
    {
        string result = string.Concat(name.Select(character =>
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 || char.IsControl(character))
            {
                return '_';
            }
            return character;
        })).Trim().TrimEnd('.');
        if (result.Length > 90)
        {
            result = result[..90].TrimEnd('.');
        }
        if (string.IsNullOrWhiteSpace(result))
        {
            result = "Wallpaper";
        }
        return result;
    }

    internal static void DeleteTemporaryDirectory(string path)
    {
        string tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WpeRemix")) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
