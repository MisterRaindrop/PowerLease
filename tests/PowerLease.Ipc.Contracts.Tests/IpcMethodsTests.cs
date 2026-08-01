using PowerLease.Ipc.Contracts;
using Xunit;

namespace PowerLease.Ipc.Contracts.Tests;

/// <summary>
/// What each request requires, kept next to the request name rather than decided at each call site. A server that
/// has to remember to check is one that will one day forget.
/// </summary>
public sealed class IpcMethodsTests
{
    [Theory]
    [InlineData(IpcMethods.GetStatus)]
    [InlineData(IpcMethods.ListLeases)]
    [InlineData(IpcMethods.CreateLease)]
    [InlineData(IpcMethods.RenewLease)]
    [InlineData(IpcMethods.ReleaseLease)]
    [InlineData(IpcMethods.GetHistory)]
    [InlineData(IpcMethods.GetWakeStatus)]
    [InlineData(IpcMethods.GetRules)]
    [InlineData(IpcMethods.GetSchedules)]
    public void An_ordinary_user_may_look_and_may_manage_their_own_holds(string method)
    {
        Assert.True(IpcMethods.IsAllowed(method, callerIsAdministrator: false));
    }

    [Theory]
    [InlineData(IpcMethods.UpdateRules)]
    [InlineData(IpcMethods.UpdateSchedules)]
    [InlineData(IpcMethods.GetLogs)]
    public void Changing_configuration_or_reading_logs_needs_an_administrator(string method)
    {
        // Configuration decides when a machine may sleep, and the logs name accounts and remote addresses.
        Assert.False(IpcMethods.IsAllowed(method, callerIsAdministrator: false));
        Assert.True(IpcMethods.IsAllowed(method, callerIsAdministrator: true));
    }

    [Fact]
    public void A_method_nobody_answers_is_refused_rather_than_defaulted()
    {
        // Defaulting it either locks out something that should work or opens something that should not.
        Assert.False(IpcMethods.IsAllowed("DeleteEverything", callerIsAdministrator: true));
        Assert.False(IpcMethods.TryGetRequiredAccess("DeleteEverything", out _));
    }

    [Fact]
    public void Method_names_are_matched_exactly()
    {
        // Ordinal, not case-insensitive: a near-miss must not silently reach a different handler.
        Assert.False(IpcMethods.IsAllowed("getstatus", callerIsAdministrator: false));
        Assert.False(IpcMethods.IsAllowed(" GetStatus", callerIsAdministrator: false));
    }

    [Fact]
    public void Every_named_method_has_an_access_level()
    {
        // Guards against a method being added to the surface without anyone deciding who may call it.
        var declared = typeof(IpcMethods)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.Equal(12, declared.Length);
        Assert.All(declared, method => Assert.True(
            IpcMethods.TryGetRequiredAccess(method, out _),
            $"'{method}' is a method with no access level"));
        Assert.Equal(declared.OrderBy(m => m), IpcMethods.All.OrderBy(m => m));
    }

    [Fact]
    public void A_refusal_carries_the_request_it_refers_to()
    {
        var requestId = Guid.NewGuid();

        var refused = ResponseEnvelope.Refused(requestId, "not permitted");

        Assert.False(refused.Accepted);
        Assert.Equal(requestId, refused.RequestId);
        Assert.Equal(IpcProtocol.Version, refused.ProtocolVersion);
        Assert.Equal("not permitted", refused.Error);
        Assert.Null(refused.PayloadJson);
    }

    [Fact]
    public void A_status_that_is_not_being_honoured_says_so_on_the_value_itself()
    {
        // So a caller cannot report success by forgetting to look at the state.
        var failing = new StatusResponse
        {
            Revision = 1,
            ProtectionState = ReportedProtectionState.Unprotected,
            ShouldHold = true
        };

        Assert.True(failing.IsProtectionFailing);

        var holding = failing with { ProtectionState = ReportedProtectionState.Protected };
        Assert.False(holding.IsProtectionFailing);

        var released = failing with { ProtectionState = ReportedProtectionState.Released, ShouldHold = false };
        Assert.False(released.IsProtectionFailing);
    }
}
