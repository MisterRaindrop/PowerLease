using System.Globalization;

namespace PowerLease.Persistence.Sqlite;

/// <summary>
/// The one way timestamps are written to and read from the database.
/// <para>
/// Always UTC, always the same round-trippable format, so text comparison in SQL orders rows the same
/// way time does. A row written in local time would sort wrongly against every other row and shift
/// silently twice a year.
/// </para>
/// </summary>
public static class Timestamps
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string ToText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return DateTimeOffset.ParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    public static string? ToTextOrNull(DateTimeOffset? value) => value is { } present ? ToText(present) : null;

    public static DateTimeOffset? ParseOrNull(string? text) =>
        string.IsNullOrEmpty(text) ? null : Parse(text);
}
