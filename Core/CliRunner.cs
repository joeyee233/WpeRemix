using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WpeRemix.Core;

public static class CliRunner
{
    public static bool IsRequested(string[] arguments)
    {
        return Array.IndexOf(arguments, "--export") >= 0;
    }

    public static async Task<int> RunAsync(string[] arguments)
    {
        var outputDirectory = KnownFolders.Downloads;
        var input = string.Empty;
        var logPath = string.Empty;
        for (var index = 0; index < arguments.Length; index++)
        {
            if (index + 1 >= arguments.Length)
            {
                continue;
            }
            if (arguments[index] == "--export")
            {
                input = arguments[++index];
            }
            else if (arguments[index] == "--output")
            {
                outputDirectory = arguments[++index];
            }
            else if (arguments[index] == "--log")
            {
                logPath = arguments[++index];
            }
        }
        if (string.IsNullOrWhiteSpace(logPath))
        {
            logPath = Path.Combine(outputDirectory, "cli-result-" + Guid.NewGuid().ToString("N") + ".json");
        }
        try
        {
            var processor = new WallpaperProcessor(new SceneComposer());
            var result = await processor.ProcessAsync(input, new ExportOptions(outputDirectory, ExportMode.CompositeAndExtract), null, CancellationToken.None);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
            await File.WriteAllTextAsync(logPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
            await File.WriteAllTextAsync(logPath, JsonSerializer.Serialize(new { Error = exception.Message, Detail = exception.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
