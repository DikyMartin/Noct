namespace Noct.Core.Settings;

/// <summary>
/// Where Noct keeps user data. Everything lives under %LocalAppData%\Noct; nothing is written into
/// the installation directory and no music file is ever modified.
/// </summary>
public static class AppPaths
{
    public const string ApplicationName = "Noct";

    /// <summary>Set to redirect all user data, for tests and measurements that must not touch real data.</summary>
    public const string DataDirectoryVariable = "NOCT_DATA_DIR";

    public static string Root { get; } = ResolveRoot();

    private static string ResolveRoot()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        return !string.IsNullOrWhiteSpace(overridden)
            ? Path.GetFullPath(overridden)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ApplicationName);
    }

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string LibraryFile => Path.Combine(Root, "library.json");

    public static string ArtworkCache => Path.Combine(Root, "artwork");

    public static string LogFile => Path.Combine(Root, "noct.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ArtworkCache);
    }
}
