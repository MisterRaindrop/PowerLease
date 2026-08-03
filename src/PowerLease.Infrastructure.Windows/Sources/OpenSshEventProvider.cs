using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using PowerLease.Application.Inhibitors;

namespace PowerLease.Infrastructure.Windows.Sources;

/// <summary>Reads authentication and disconnect records from the Windows OpenSSH operational log.</summary>
public sealed partial class OpenSshEventProvider : ISshAuthLogReader
{
    private const string ChannelName = "OpenSSH/Operational";
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorEvtInvalidChannelPath = 15000;
    private const int ErrorEvtChannelNotFound = 15007;
    private const int ErrorEvtMalformedXmlText = 15008;
    private const int ErrorEvtQueryResultStale = 15011;
    private const int ErrorEvtQueryResultInvalidPosition = 15012;
    private const string StoredBookmarkPrefix = "powerlease-ssh-v1:";

    private readonly object _sync = new();
    private string? _currentIncarnation;
    private string? _reseedDetail;
    private string? _reseedIncarnation;
    private string? _reseededFromBookmark;
    private bool _reseedPending;

    /// <summary>
    /// The discontinuity recovered by the most recent read. The service latches this as a persistent fault until
    /// the replacement bookmark and every hold implied by the replacement log have been made durable.
    /// </summary>
    public string? LastReadDiscontinuityDetail { get; private set; }

    public SshLogRead Read(string? bookmark)
    {
        lock (_sync)
        {
            LastReadDiscontinuityDetail = null;
            if (bookmark is not null
                && _reseedPending
                && string.Equals(bookmark, _reseededFromBookmark, StringComparison.Ordinal))
            {
                return Reseed();
            }

            BookmarkPosition position;
            try
            {
                position = DecodeBookmark(bookmark);
            }
            catch (ArgumentException exception) when (bookmark is not null)
            {
                return BeginReseed(
                    bookmark,
                    $"The stored OpenSSH bookmark could not be reconstructed: {exception.Message}");
            }

            if (bookmark is null)
            {
                _currentIncarnation ??= Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            }
            else
            {
                _currentIncarnation = position.Incarnation;
            }

            var read = ReadAttempt(position.BookmarkXml, _currentIncarnation, bookmark is not null);
            return bookmark is not null && read.State == SshLogChannelState.Discontinuous
                ? BeginReseed(bookmark, read.Detail ?? "The stored OpenSSH bookmark is no longer valid.")
                : read;
        }
    }

    private SshLogRead ReadAttempt(string? bookmarkXml, string? incarnation, bool hasStoredBookmark)
    {
        try
        {
            return ReadCore(bookmarkXml, incarnation);
        }
        catch (EventLogNotFoundException exception)
        {
            return SshLogRead.Unavailable(
                SshLogChannelState.NotInstalled,
                $"The '{ChannelName}' event log was not found: {exception.Message}");
        }
        catch (EventLogReadingException exception)
        {
            return SshLogRead.Unavailable(
                SshLogChannelState.Discontinuous,
                $"The '{ChannelName}' event log changed while it was being read: {exception.Message}");
        }
        catch (EventLogInvalidDataException exception) when (hasStoredBookmark)
        {
            return SshLogRead.Unavailable(
                SshLogChannelState.Discontinuous,
                $"The stored OpenSSH bookmark is no longer valid: {exception.Message}");
        }
        catch (ArgumentException exception) when (hasStoredBookmark)
        {
            return SshLogRead.Unavailable(
                SshLogChannelState.Discontinuous,
                $"The stored OpenSSH bookmark could not be reconstructed: {exception.Message}");
        }
        catch (EventLogException exception) when (hasStoredBookmark && IsBookmarkFailure(exception))
        {
            return SshLogRead.Unavailable(
                SshLogChannelState.Discontinuous,
                $"The stored OpenSSH bookmark could not be resolved: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return SshLogRead.Unavailable(
                SshLogChannelState.TransientFailure,
                $"Access to the '{ChannelName}' event log was denied: {exception.Message}");
        }
        catch (SecurityException exception)
        {
            return SshLogRead.Unavailable(
                SshLogChannelState.TransientFailure,
                $"Security policy prevented reading the '{ChannelName}' event log: {exception.Message}");
        }
        catch (EventLogException exception)
        {
            return SshLogRead.Unavailable(
                SshLogChannelState.TransientFailure,
                $"The '{ChannelName}' event log could not be read: {exception.Message}");
        }
        catch (IOException exception)
        {
            return SshLogRead.Unavailable(
                SshLogChannelState.TransientFailure,
                $"The '{ChannelName}' event log could not be read: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return SshLogRead.Unavailable(
                SshLogChannelState.TransientFailure,
                $"The '{ChannelName}' event log reader was not usable: {exception.Message}");
        }
    }

    private SshLogRead BeginReseed(string storedBookmark, string discontinuity)
    {
        if (!string.Equals(storedBookmark, _reseededFromBookmark, StringComparison.Ordinal))
        {
            _reseededFromBookmark = storedBookmark;
            _reseedIncarnation = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        }

        _reseedIncarnation ??= Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _currentIncarnation = _reseedIncarnation;

        _reseedDetail =
            $"{discontinuity} PowerLease will re-seed from the oldest retained record while holding awake.";
        _reseedPending = true;
        LastReadDiscontinuityDetail = _reseedDetail;
        return SshLogRead.Unavailable(SshLogChannelState.Discontinuous, _reseedDetail);
    }

    private SshLogRead Reseed()
    {
        LastReadDiscontinuityDetail = _reseedDetail
            ?? "The OpenSSH log position was lost. PowerLease is re-seeding while holding awake.";
        var reseeded = ReadAttempt(null, _currentIncarnation, hasStoredBookmark: false);
        if (reseeded.State == SshLogChannelState.Available)
        {
            _reseedPending = false;
            return reseeded;
        }

        return SshLogRead.Unavailable(
            SshLogChannelState.Discontinuous,
            $"{LastReadDiscontinuityDetail} Re-seeding could not finish: {reseeded.Detail}");
    }

    private SshLogRead ReadCore(string? bookmarkXml, string? incarnation)
    {
        EventBookmark? startingBookmark = bookmarkXml is null ? null : new EventBookmark(bookmarkXml);
        var query = new EventLogQuery(ChannelName, PathType.LogName, "*");

        using var reader = startingBookmark is null
            ? new EventLogReader(query)
            : new EventLogReader(query, startingBookmark);

        var events = new List<SshAuthEvent>();
        var nextBookmark = startingBookmark?.BookmarkXml;

        while (reader.ReadEvent() is { } record)
        {
            using (record)
            {
                if (ParseEvent(record, incarnation) is { } parsed)
                {
                    events.Add(parsed);
                }

                nextBookmark = record.Bookmark.BookmarkXml;
            }
        }

        if (ReadLogFailure(reader) is { } failure)
        {
            return failure;
        }

        return SshLogRead.Available(events, EncodeBookmark(nextBookmark, incarnation));
    }

    private static SshLogRead? ReadLogFailure(EventLogReader reader)
    {
        foreach (var status in reader.LogStatus)
        {
            if (status.StatusCode == 0)
            {
                continue;
            }

            var detail = $"Reading '{status.LogName}' failed with event-log error {status.StatusCode}.";
            if (status.StatusCode is ErrorFileNotFound or ErrorPathNotFound or ErrorEvtChannelNotFound)
            {
                return SshLogRead.Unavailable(SshLogChannelState.NotInstalled, detail);
            }

            if (status.StatusCode is ErrorEvtQueryResultStale or ErrorEvtQueryResultInvalidPosition)
            {
                return SshLogRead.Unavailable(SshLogChannelState.Discontinuous, detail);
            }

            return SshLogRead.Unavailable(SshLogChannelState.TransientFailure, detail);
        }

        return null;
    }

    private static SshAuthEvent? ParseEvent(EventRecord record, string? incarnation)
    {
        var message = ReadMessage(record);
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        SshAuthEventKind kind;
        Match match;

        match = AuthenticationMessage().Match(message);
        if (match.Success)
        {
            kind = SshAuthEventKind.Authenticated;
        }
        else
        {
            match = DisconnectedFromMessage().Match(message);
            if (match.Success)
            {
                kind = SshAuthEventKind.Disconnected;
            }
            else
            {
                match = ConnectionClosedMessage().Match(message);
                if (match.Success)
                {
                    kind = SshAuthEventKind.Disconnected;
                }
                else
                {
                    match = ReceivedDisconnectMessage().Match(message);
                    if (!match.Success)
                    {
                        return null;
                    }

                    kind = SshAuthEventKind.Disconnected;
                }
            }
        }

        if (record.RecordId is not { } recordId || record.TimeCreated is not { } occurredAt)
        {
            throw new InvalidDataException(
                "An OpenSSH authentication event did not contain a record identifier and timestamp.");
        }

        var userName = GroupValue(match, "user");
        var remoteAddress = GroupValue(match, "address")?.Trim('[', ']');
        var remotePort = ParsePort(GroupValue(match, "port"));

        var localRecordId = recordId.ToString(CultureInfo.InvariantCulture);
        return new SshAuthEvent(
            incarnation is null ? localRecordId : $"{incarnation}:{localRecordId}",
            kind,
            new DateTimeOffset(occurredAt).ToUniversalTime(),
            userName,
            remoteAddress,
            remotePort);
    }

    private static string? ReadMessage(EventRecord record)
    {
        try
        {
            var formatted = record.FormatDescription();
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                return formatted;
            }
        }
        catch (EventLogException)
        {
            // OpenSSH also puts its text in the event payload, which remains usable without provider metadata.
        }
        catch (InvalidOperationException)
        {
            // Fall through to the raw payload.
        }

        return record.Properties
            .Select(property => property.Value as string)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? GroupValue(Match match, string groupName)
    {
        var group = match.Groups[groupName];
        return group.Success ? group.Value : null;
    }

    private static int? ParsePort(string? value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is > 0 and <= ushort.MaxValue
                ? port
                : null;
    }

    private static bool IsBookmarkFailure(EventLogException exception)
    {
        var error = exception.HResult & 0x0000ffff;
        return error is ErrorInvalidParameter
            or ErrorEvtInvalidChannelPath
            or ErrorEvtMalformedXmlText
            or ErrorEvtQueryResultStale
            or ErrorEvtQueryResultInvalidPosition;
    }

    private static BookmarkPosition DecodeBookmark(string? stored)
    {
        if (stored is null)
        {
            return new BookmarkPosition(null, null);
        }

        if (!stored.StartsWith(StoredBookmarkPrefix, StringComparison.Ordinal))
        {
            // Bookmarks written by earlier versions used the event-log XML directly and numeric record IDs.
            return new BookmarkPosition(stored, null);
        }

        var encoded = stored[StoredBookmarkPrefix.Length..];
        var separator = encoded.IndexOf(':');
        if (separator <= 0
            || !Guid.TryParseExact(encoded[..separator], "N", out var incarnation))
        {
            throw new ArgumentException("The PowerLease bookmark envelope is malformed.", nameof(stored));
        }

        try
        {
            var bookmarkBytes = Convert.FromBase64String(encoded[(separator + 1)..]);
            var bookmarkXml = bookmarkBytes.Length == 0 ? null : Encoding.UTF8.GetString(bookmarkBytes);
            return new BookmarkPosition(bookmarkXml, incarnation.ToString("N", CultureInfo.InvariantCulture));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The PowerLease bookmark envelope is malformed.", nameof(stored), exception);
        }
    }

    private static string? EncodeBookmark(string? bookmarkXml, string? incarnation)
    {
        if (incarnation is null)
        {
            return bookmarkXml;
        }

        var bookmarkBytes = bookmarkXml is null ? [] : Encoding.UTF8.GetBytes(bookmarkXml);
        return StoredBookmarkPrefix + incarnation + ":" + Convert.ToBase64String(bookmarkBytes);
    }

    private sealed record BookmarkPosition(string? BookmarkXml, string? Incarnation);

    [GeneratedRegex(
        @"Accepted\s+\S+\s+for\s+(?<user>\S+)\s+from\s+(?<address>\S+)\s+port\s+(?<port>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthenticationMessage();

    [GeneratedRegex(
        @"Disconnected\s+from\s+(?:(?:invalid|authenticating)\s+user\s+|user\s+)?(?<user>\S+)\s+" +
        @"(?<address>\S+)\s+port\s+(?<port>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DisconnectedFromMessage();

    [GeneratedRegex(
        @"Connection\s+closed\s+by\s+(?:(?:(?:invalid|authenticating)\s+user|user)\s+(?<user>\S+)\s+)?" +
        @"(?<address>\S+)\s+port\s+(?<port>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionClosedMessage();

    [GeneratedRegex(
        @"Received\s+disconnect\s+from\s+(?<address>\S+)\s+port\s+(?<port>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReceivedDisconnectMessage();
}
