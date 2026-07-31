namespace PowerLease.Persistence;

public sealed class PowerLeasePaths
{
    public PowerLeasePaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A root directory path is required.", nameof(root));
        }

        Root = root;
        ConfigFilePath = Path.Combine(root, "config.json");
        LastGoodConfigFilePath = Path.Combine(root, "config.last-good.json");
        ConfigTempFilePath = Path.Combine(root, "config.json.tmp");
        DatabasePath = Path.Combine(root, "data", "powerlease.db");
        LogsDirectory = Path.Combine(root, "logs");
        LockFilePath = Path.Combine(root, "keep-awake.lock");
        BackupsDirectory = Path.Combine(root, "backups");
    }

    public string Root { get; }

    public string ConfigFilePath { get; }

    /// <summary>
    /// The last configuration that loaded and validated cleanly. Written as a side effect of the
    /// atomic replace that installs a new configuration, so it is always a file that was live.
    /// </summary>
    public string LastGoodConfigFilePath { get; }

    /// <summary>
    /// Scratch file a new configuration is written to before being moved into place. Never read
    /// back: a leftover one means a previous write was interrupted, and its contents are unknown.
    /// </summary>
    public string ConfigTempFilePath { get; }

    public string DatabasePath { get; }

    public string LogsDirectory { get; }

    public string LockFilePath { get; }

    public string BackupsDirectory { get; }

    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PowerLease");

    public static PowerLeasePaths Default() => new(DefaultRoot);
}
