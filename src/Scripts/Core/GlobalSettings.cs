using System.Reflection;

namespace Jasnote.Core;

public sealed class GlobalSettings
{
    // Use SemVer 2.0 format without the leading 'v'
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public const string AppInfo = "Jasnote.AppInfo.json";
    public const string AppIcon = "Jasnote.Icons.Logo.ico";
}
