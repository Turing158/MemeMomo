using System;
using System.IO;

namespace MemeMomo.Services;

/// <summary>
/// Resolves the application's persistent data root. The environment override is
/// intentionally opt-in so production continues to use %AppData%\MemeMomo while
/// isolated acceptance/rehearsal processes can never touch a user's data.
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
