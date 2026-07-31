using PowerLease.Domain;

namespace PowerLease.Application.Inhibitors;

/// <summary>Whether the keep-awake lock file is there.</summary>
/// <param name="Succeeded">False when the check itself failed, which says nothing about the file.</param>
public sealed record LockFileProbe(bool Succeeded, bool Exists, string? Detail = null)
{
    public static LockFileProbe Present() => new(true, true);

    public static LockFileProbe Absent() => new(true, false);

    public static LockFileProbe Unavailable(string detail) => new(false, false, detail);
}

public interface ILockFileProbe
{
    LockFileProbe Check(string path);
}

/// <summary>
/// Holds the machine awake while a file exists.
/// <para>
/// The documented way for a script to protect itself: create the file, do the work, delete it. Deliberately
/// the crudest mechanism in the product, because it is the one that has to work from a batch file with no
/// knowledge of PowerLease at all.
/// </para>
/// </summary>
public sealed class LockFileEvaluator
{
    public const string SourceId = "lock-file";

    private readonly string? _path;

    /// <param name="path">Where to look, or null to switch the rule off.</param>
    public LockFileEvaluator(string? path)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public InhibitorSourceReport Evaluate(ILockFileProbe probe, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (_path is null)
        {
            return InhibitorSourceReport.ConfirmedAbsent(SourceId);
        }

        var result = probe.Check(_path);

        if (!result.Succeeded)
        {
            // A failed check is not evidence the file is gone. Someone may have created it precisely because
            // they are about to start something long.
            return InhibitorSourceReport.Indeterminate(
                SourceId, $"Could not check for '{_path}': {result.Detail}");
        }

        return result.Exists
            ? InhibitorSourceReport.Observed(
                SourceId,
                new Inhibitor(InhibitorKind.LockFile, $"'{_path}' exists", nowUtc, _path))
            : InhibitorSourceReport.ConfirmedAbsent(SourceId);
    }
}
