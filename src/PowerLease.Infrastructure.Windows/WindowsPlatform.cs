namespace PowerLease.Infrastructure.Windows;

public static class WindowsPlatform
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    public static Version OsVersion => Environment.OSVersion.Version;

    public static string Describe()
    {
        return IsSupported
            ? $"Windows {OsVersion}"
            : Environment.OSVersion.ToString();
    }
}
