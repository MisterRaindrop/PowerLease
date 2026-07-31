namespace PowerLease.Persistence.Configuration;

/// <summary>Where the configuration in use actually came from.</summary>
public enum ConfigSource
{
    /// <summary>Nothing was on disk yet. Built-in defaults are in use.</summary>
    Defaults,

    /// <summary><c>config.json</c> loaded and validated cleanly.</summary>
    File,

    /// <summary>
    /// <c>config.json</c> was unusable and the last known good file was used instead. The user's
    /// most recent edit is not in effect, so this always comes with problems to report and a latched
    /// fault.
    /// </summary>
    LastGood
}
