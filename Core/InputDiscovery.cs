using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace WpeRemix.Core;

public static class InputDiscovery
{
    public static IReadOnlyList<string> Discover(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in paths)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }
            string fullPath = Path.GetFullPath(candidate);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                throw new FileNotFoundException("找不到输入文件或文件夹", fullPath);
            }
            SafeFiles.RejectLinks(fullPath);
            if (File.Exists(fullPath))
            {
                string extension = Path.GetExtension(fullPath);
                if (extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tex", StringComparison.OrdinalIgnoreCase) || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || SafeFiles.IsImage(fullPath))
                {
                    results.Add(fullPath);
                }
                continue;
            }
            var files = SafeFiles.EnumerateFiles(fullPath, CancellationToken.None);
            string[] packages = files.Where(path => Path.GetExtension(path).Equals(".pkg", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (packages.Length > 0)
            {
                foreach (string package in packages)
                {
                    results.Add(package);
                }
                continue;
            }
            string[] scenes = files.Where(path => Path.GetFileName(path).Equals("scene.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (scenes.Length > 0)
            {
                foreach (string scene in scenes)
                {
                    results.Add(Path.GetDirectoryName(scene)!);
                }
                continue;
            }
            foreach (string file in files)
            {
                if (Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(file);
                }
            }
            if (!files.Any(path => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)) && files.Any(path => SafeFiles.IsImage(path) || Path.GetExtension(path).Equals(".tex", StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(fullPath);
            }
        }
        return results.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
