using System.Reflection;

namespace ParleyCli;

internal static class ParleyVersion
{
    private static readonly Assembly Assembly = typeof(ParleyVersion).Assembly;

    public static string Display =>
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
        ?? "unknown";

    public static Version? Numeric
    {
        get
        {
            var value = Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            return Version.TryParse(value, out var version) ? version : null;
        }
    }
}
