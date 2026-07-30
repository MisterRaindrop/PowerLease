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
        DatabasePath = Path.Combine(root, "data", "powerlease.db");
        LogsDirectory = Path.Combine(root, "logs");
        LockFilePath = Path.Combine(root, "keep-awake.lock");
        BackupsDirectory = Path.Combine(root, "backups");
    }

    public string Root { get; }

    public string ConfigFilePath { get; }

    public string DatabasePath { get; }

    public string LogsDirectory { get; }

    public string LockFilePath { get; }

    public string BackupsDirectory { get; }

    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PowerLease");

    public static PowerLeasePaths Default() => new(DefaultRoot);
}
