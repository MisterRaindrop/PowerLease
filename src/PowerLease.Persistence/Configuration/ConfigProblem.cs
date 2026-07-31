namespace PowerLease.Persistence.Configuration;

/// <summary>
/// Something wrong with a configuration file, named precisely enough that the user can find it.
/// </summary>
/// <param name="Path">Dotted path of the offending field, for example <c>ssh.ports[1]</c>.</param>
/// <param name="Message">What is wrong and what to do about it.</param>
public sealed record ConfigProblem(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}
