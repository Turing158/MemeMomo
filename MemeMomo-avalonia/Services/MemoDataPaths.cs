using System;
using System.IO;

namespace MemeMomo.Services;

/// <summary>
/// Resolves the Avalonia application's persistent data root and its test override.
/// </summary>
internal static class MemoDataPaths
{
    internal const string AppDataOverrideVariable = "MEMEMOMO_APPDATA_DIR";

    internal static string RootDirectory
    {
        get
        {
            string? appData = Environment.GetEnvironmentVariable(AppDataOverrideVariable);
            if (string.IsNullOrWhiteSpace(appData))
            {
                appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }

            return Path.Combine(appData, "MemeMomo");
        }
    }
}
