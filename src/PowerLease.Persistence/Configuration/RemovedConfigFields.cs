using System.Text.Json;

namespace PowerLease.Persistence.Configuration;

/// <summary>
/// Fields the specification describes that this version deliberately does not have.
/// <para>
/// They are rejected rather than ignored. Every one of them describes the machine being put to
/// sleep, woken, or watched in a way this version does not do, so accepting one silently would let
/// a user believe a setting had taken effect when nothing will happen. This list is also the
/// machine-readable record of where the product deviates from the specification.
/// </para>
/// </summary>
public static class RemovedConfigFields
{
    private static readonly (string Path, string Explanation)[] Entries =
    [
        ("general.dryRun",
            "Removed. This version performs no power transitions, so there is nothing to simulate. " +
            "A version that can sleep the machine will introduce a new field that defaults to off; " +
            "permission to act will never be inferred from this one."),
        ("idleRules.candidateObservationMinutes",
            "Removed. There is no idle-candidate state, because this version does not decide when to sleep."),
        ("idleRules.preSleepCountdownSeconds",
            "Removed. This version never initiates sleep, so there is no countdown to cancel."),
        ("idleRules.userInput",
            "Removed. Windows already resets its own idle timer on keyboard and mouse input, so this " +
            "rule duplicated something the operating system does better."),
        ("powerPolicy",
            "Removed. When to sleep or hibernate is left entirely to the Windows power plan; this " +
            "version only holds the machine awake."),
        ("wake",
            "Not in this version. Scheduled wake-up and Wake-on-LAN are not implemented."),
        ("temperature",
            "Not in this version. Temperature monitoring is not implemented."),
        ("notifications",
            "Not in this version. There is no desktop component to show a notification.")
    ];

    /// <summary>The dotted paths that are rejected, for documentation and tests.</summary>
    public static IReadOnlyList<string> Paths { get; } = [.. Entries.Select(entry => entry.Path)];

    /// <summary>
    /// Report every removed field present in <paramref name="document" />.
    /// </summary>
    public static IReadOnlyList<ConfigProblem> Detect(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var problems = new List<ConfigProblem>();
        foreach (var (path, explanation) in Entries)
        {
            if (Contains(document.RootElement, path))
            {
                problems.Add(new ConfigProblem(path, explanation));
            }
        }

        return problems;
    }

    private static bool Contains(JsonElement root, string dottedPath)
    {
        var element = root;
        foreach (var segment in dottedPath.Split('.'))
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out var child))
            {
                return false;
            }

            element = child;
        }

        return true;
    }
}
