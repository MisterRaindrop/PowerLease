namespace PowerLease.Persistence.Configuration;

/// <summary>
/// Thrown when a configuration is asked to be written but would not be valid to read back.
/// </summary>
public sealed class ConfigValidationException : Exception
{
    public ConfigValidationException(IReadOnlyList<ConfigProblem> problems)
        : base(BuildMessage(problems))
    {
        Problems = problems;
    }

    public ConfigValidationException()
        : this([])
    {
    }

    public ConfigValidationException(string message)
        : base(message)
    {
        Problems = [];
    }

    public ConfigValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Problems = [];
    }

    public IReadOnlyList<ConfigProblem> Problems { get; }

    private static string BuildMessage(IReadOnlyList<ConfigProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        return problems.Count == 0
            ? "The configuration is not valid."
            : "The configuration is not valid:" + Environment.NewLine +
              string.Join(Environment.NewLine, problems.Select(problem => "  " + problem));
    }
}
