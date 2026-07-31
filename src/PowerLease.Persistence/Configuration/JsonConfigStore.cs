using System.Text.Json;

namespace PowerLease.Persistence.Configuration;

/// <summary>
/// Reads and writes <c>config.json</c>.
/// <para>
/// Writing goes through a temporary file and an atomic replace, so a crash or a full disk can never
/// leave a half-written configuration behind. The replace also moves the file being displaced to
/// <see cref="PowerLeasePaths.LastGoodConfigFilePath" /> in the same operation, which is what makes
/// the fallback trustworthy: the last good file is always one that was live, not a copy taken at
/// some other moment.
/// </para>
/// <para>
/// Reading never fails. An unusable file falls back to the last good one and then to defaults, and
/// reports what went wrong so the caller can latch a fault. Refusing to start would leave the
/// machine with no keep-awake protection at all, which is the outcome this product exists to
/// prevent.
/// </para>
/// </summary>
public sealed class JsonConfigStore
{
    // Reflection-based serialisation is deliberate. The configuration is read once at startup, and
    // neither published executable is trimmed or compiled ahead of time. Enabling either would
    // require switching to a source-generated context.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly PowerLeasePaths _paths;

    public JsonConfigStore(PowerLeasePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>
    /// Load the configuration, falling back as far as necessary and reporting every fallback.
    /// </summary>
    public ConfigLoadResult Load()
    {
        var primary = Read(_paths.ConfigFilePath);

        if (primary.Config is { } loaded)
        {
            return new ConfigLoadResult(loaded, ConfigSource.File, []);
        }

        var fallback = Read(_paths.LastGoodConfigFilePath);

        if (fallback.Config is { } lastGood)
        {
            var problems = new List<ConfigProblem>(primary.Problems);
            if (primary.Missing)
            {
                problems.Add(new ConfigProblem(
                    _paths.ConfigFilePath,
                    "The file is missing. The last known good configuration is being used instead."));
            }

            return new ConfigLoadResult(lastGood, ConfigSource.LastGood, problems);
        }

        if (primary.Missing && fallback.Missing)
        {
            // First run. Nothing is wrong, so nothing is reported and no fault is latched.
            return new ConfigLoadResult(new PowerLeaseConfig(), ConfigSource.Defaults, []);
        }

        var combined = new List<ConfigProblem>(primary.Problems);
        combined.AddRange(fallback.Problems);
        return new ConfigLoadResult(new PowerLeaseConfig(), ConfigSource.Defaults, combined);
    }

    /// <summary>
    /// Validate and write a configuration.
    /// </summary>
    /// <exception cref="ConfigValidationException">
    /// Thrown before anything is written when the configuration would not be valid to read back.
    /// The file on disk is left exactly as it was.
    /// </exception>
    public void Save(PowerLeaseConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var problems = ConfigValidator.Validate(config);
        if (problems.Count > 0)
        {
            throw new ConfigValidationException(problems);
        }

        Directory.CreateDirectory(_paths.Root);

        // Truncate rather than append, because a temporary file may be left over from a write that
        // was interrupted and its contents mean nothing.
        using (var stream = new FileStream(
            _paths.ConfigTempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, config, SerializerOptions);

            // Get the bytes onto the disk before the file is moved into place. Without this the
            // replace can be durable while its contents are not, which is the one ordering that
            // produces a valid-looking but empty configuration after a power cut.
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_paths.ConfigFilePath))
        {
            File.Replace(_paths.ConfigTempFilePath, _paths.ConfigFilePath, _paths.LastGoodConfigFilePath);
        }
        else
        {
            File.Move(_paths.ConfigTempFilePath, _paths.ConfigFilePath);
        }
    }

    private static ReadOutcome Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return ReadOutcome.NotFound();
        }
        catch (DirectoryNotFoundException)
        {
            return ReadOutcome.NotFound();
        }
        catch (IOException error)
        {
            return ReadOutcome.Unusable([new ConfigProblem(path, $"Could not be read: {error.Message}")]);
        }
        catch (UnauthorizedAccessException error)
        {
            return ReadOutcome.Unusable([new ConfigProblem(path, $"Could not be read: {error.Message}")]);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException error)
        {
            return ReadOutcome.Unusable([new ConfigProblem(path, $"Is not valid JSON: {error.Message}")]);
        }

        using (document)
        {
            // Removed fields are looked for before deserialising, so they produce an explanation of
            // what happened to the setting rather than a bare complaint about an unknown property.
            var removed = RemovedConfigFields.Detect(document);
            if (removed.Count > 0)
            {
                return ReadOutcome.Unusable(removed);
            }
        }

        PowerLeaseConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<PowerLeaseConfig>(text, SerializerOptions);
        }
        catch (JsonException error)
        {
            return ReadOutcome.Unusable([new ConfigProblem(path, error.Message)]);
        }

        if (config is null)
        {
            return ReadOutcome.Unusable([new ConfigProblem(path, "Contains only a JSON null.")]);
        }

        var problems = ConfigValidator.Validate(config);
        return problems.Count > 0 ? ReadOutcome.Unusable(problems) : ReadOutcome.Loaded(config);
    }

    private sealed class ReadOutcome
    {
        private ReadOutcome(PowerLeaseConfig? config, bool missing, IReadOnlyList<ConfigProblem> problems)
        {
            Config = config;
            Missing = missing;
            Problems = problems;
        }

        public PowerLeaseConfig? Config { get; }

        /// <summary>No file at all, which on a first run is expected rather than a problem.</summary>
        public bool Missing { get; }

        public IReadOnlyList<ConfigProblem> Problems { get; }

        public static ReadOutcome Loaded(PowerLeaseConfig config) => new(config, missing: false, []);

        public static ReadOutcome NotFound() => new(config: null, missing: true, []);

        public static ReadOutcome Unusable(IReadOnlyList<ConfigProblem> problems) =>
            new(config: null, missing: false, problems);
    }
}
