using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace WpeRemix.Core;

public static class WallpaperLocations
{
    public static string InputDirectory
    {
        get
        {
            string configuredWorkshop = @"C:\Applications\Steam\steamapps\workshop\content\431960";
            if (Directory.Exists(configuredWorkshop))
            {
                return configuredWorkshop;
            }
            try
            {
                using RegistryKey? steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (steam?.GetValue("SteamPath") is string steamPath)
                {
                    string workshop = Path.Combine(steamPath, "steamapps", "workshop", "content", "431960");
                    if (Directory.Exists(workshop))
                    {
                        return workshop;
                    }
                }
            }
            catch (Exception exception) when (exception is SecurityException || exception is UnauthorizedAccessException || exception is IOException || exception is ArgumentException)
            {
                // A missing or inaccessible Steam installation uses the standard import folder.
            }
            return KnownFolders.Downloads;
        }
    }
}
