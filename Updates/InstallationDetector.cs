namespace ParleyCli.Updates;

internal static class InstallationDetector
{
    internal static string? UpgradeCommand(
        string executablePath,
        string userProfile,
        Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;
        var normalized = executablePath.Replace('\\', '/');

        if (normalized.Contains("/Cellar/parley/", StringComparison.Ordinal))
            return "brew upgrade parley";

        if (normalized.Contains("/scoop/apps/parley/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/scoop/shims/parley", StringComparison.OrdinalIgnoreCase))
            return "scoop update parley";

        var executableDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(executableDirectory)
            || !directoryExists(Path.Combine(executableDirectory, ".store", "parley-cli")))
            return null;

        var globalTools = Path.GetFullPath(Path.Combine(userProfile, ".dotnet", "tools"));
        if (Path.GetFullPath(executableDirectory).Equals(
                globalTools,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return "dotnet tool update --global parley-cli";

        return $"dotnet tool update --tool-path \"{executableDirectory}\" parley-cli";
    }

    internal static string? CurrentUpgradeCommand()
    {
        if (Environment.ProcessPath is not { } executablePath)
            return null;

        try
        {
            var resolved = File.ResolveLinkTarget(executablePath, returnFinalTarget: true);
            if (resolved is not null)
                executablePath = resolved.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original path still carries enough information for Scoop and custom .NET tools.
        }

        return UpgradeCommand(
            executablePath,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }
}
