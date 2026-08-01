using System.Text.Json;
using Microsoft.Data.Sqlite;
using PowerLease.Application.Hosting;
using PowerLease.Application.Kernel;
using PowerLease.Domain;

namespace PowerLease.Persistence.History;

/// <summary>Carries out the kernel's durable effects against the history database.</summary>
public sealed class SqliteEffectExecutor : IEffectExecutor
{
    private readonly SqliteHistoryStore _store;
    private readonly IClock _clock;

    public SqliteEffectExecutor(SqliteHistoryStore store, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _clock = clock;
    }

    public Task<EffectCompletion> ExecuteAsync(KernelEffect effect, CancellationToken cancellationToken)
    {
        if (effect is null)
        {
            return Task.FromResult(Failed(0, "Effect was missing."));
        }

        return Task.FromResult(Execute(effect, cancellationToken));
    }

    private EffectCompletion Execute(KernelEffect effect, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            return effect.Kind switch
            {
                EffectKind.PersistLease => PersistLease(effect, commandRequired: false),
                EffectKind.PersistLeaseRelease => PersistLease(effect, commandRequired: true),
                EffectKind.RecordInhibitChange => RecordInhibitChange(effect),
                _ => Failed(effect.EffectId, $"Effect kind '{effect.Kind}' is not supported.")
            };
        }
        catch (SqliteException error)
        {
            return Failed(effect.EffectId, error.Message);
        }
        catch (InvalidOperationException error)
        {
            return Failed(effect.EffectId, error.Message);
        }
        catch (OperationCanceledException error)
        {
            return Failed(effect.EffectId, error.Message);
        }
    }

    private EffectCompletion PersistLease(KernelEffect effect, bool commandRequired)
    {
        if (effect.Lease is not { } lease)
        {
            return Failed(effect.EffectId, $"{effect.Kind} requires Lease.");
        }

        if (effect.RequestId is null)
        {
            if (commandRequired)
            {
                return Failed(effect.EffectId, $"{effect.Kind} requires RequestId.");
            }

            using var expiryTransaction = _store.BeginTransaction();
            expiryTransaction.UpsertLease(lease);
            expiryTransaction.Commit();
            return new EffectCompletion(effect.EffectId, EffectOutcome.Succeeded);
        }

        if (effect.RequestId.Length == 0)
        {
            return Failed(effect.EffectId, $"{effect.Kind} requires a non-empty RequestId.");
        }

        if (effect.Caller is not { } caller)
        {
            return Failed(effect.EffectId, $"{effect.Kind} with a RequestId requires Caller.");
        }

        if (string.IsNullOrEmpty(caller.Sid))
        {
            return Failed(effect.EffectId, $"{effect.Kind} with a RequestId requires Caller.Sid.");
        }

        if (string.IsNullOrEmpty(effect.PayloadHash))
        {
            return Failed(effect.EffectId, $"{effect.Kind} with a RequestId requires PayloadHash.");
        }

        var resultJson = JsonSerializer.Serialize(new { leaseId = lease.Id });
        using var transaction = _store.BeginTransaction();
        var command = transaction.RecordCommand(
            caller.Sid,
            effect.RequestId,
            effect.PayloadHash,
            resultJson,
            _clock.UtcNow);

        switch (command.Outcome)
        {
            case CommandRecordOutcome.Recorded:
                transaction.UpsertLease(lease);
                transaction.Commit();
                return new EffectCompletion(effect.EffectId, EffectOutcome.Succeeded, resultJson);

            case CommandRecordOutcome.AlreadyCompleted:
                return new EffectCompletion(
                    effect.EffectId,
                    EffectOutcome.AlreadyDone,
                    command.ExistingResultJson);

            case CommandRecordOutcome.PayloadConflict:
                return new EffectCompletion(effect.EffectId, EffectOutcome.Conflict);

            default:
                return Failed(effect.EffectId, $"Command record outcome '{command.Outcome}' is not supported.");
        }
    }

    private EffectCompletion RecordInhibitChange(KernelEffect effect)
    {
        if (effect.InhibitorKinds is null)
        {
            return Failed(effect.EffectId, $"{effect.Kind} requires InhibitorKinds.");
        }

        var change = effect.ProtectionState switch
        {
            ProtectionState.Protected => InhibitChange.Established,
            ProtectionState.Released => InhibitChange.Released,
            ProtectionState.Unprotected => InhibitChange.Rejected,
            _ => (InhibitChange?)null
        };

        if (change is null)
        {
            return Failed(
                effect.EffectId,
                $"Protection state '{effect.ProtectionState}' is not supported.");
        }

        using var transaction = _store.BeginTransaction();
        transaction.RecordInhibitEvent(new InhibitEvent
        {
            OccurredAtUtc = _clock.UtcNow,
            Change = change.Value,
            ProtectionState = effect.ProtectionState,
            InhibitorKinds = effect.InhibitorKinds,
            Reason = effect.Reason,
            Revision = effect.Revision
        });
        transaction.Commit();

        return new EffectCompletion(effect.EffectId, EffectOutcome.Succeeded);
    }

    private static EffectCompletion Failed(long effectId, string error) =>
        new(effectId, EffectOutcome.Failed, Error: error);
}
