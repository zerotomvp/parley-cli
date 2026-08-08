using System.Diagnostics;
using System.Reflection;

namespace ParleyCli;

internal static class ParleyVersion
{
    private static readonly Assembly Assembly = typeof(ParleyVersion).Assembly;

    public static string Display =>
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? FileVersionInfo.GetVersionInfo(Assembly.Location).FileVersion
        ?? "unknown";

    public static Version? Numeric
    {
        get
        {
            var value = FileVersionInfo.GetVersionInfo(Assembly.Location).FileVersion;
            return Version.TryParse(value, out var version) ? version : null;
        }
    }
}
