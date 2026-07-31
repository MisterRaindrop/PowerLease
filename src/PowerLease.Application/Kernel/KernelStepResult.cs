namespace PowerLease.Application.Kernel;

/// <summary>
/// Everything one turn of the loop produced: the new published state, the durable work the host must
/// now carry out, and the answers owed to callers.
/// </summary>
public sealed class KernelStepResult
{
    public KernelStepResult(
        KernelSnapshot snapshot,
        IReadOnlyList<KernelEffect> effects,
        IReadOnlyList<LeaseCommandResult> completedCommands,
        bool protectionChanged)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(completedCommands);

        Snapshot = snapshot;
        Effects = effects;
        CompletedCommands = completedCommands;
        ProtectionChanged = protectionChanged;
    }

    public KernelSnapshot Snapshot { get; }

    /// <summary>Durable work to perform off the loop, then report back with <see cref="EffectFinished" />.</summary>
    public IReadOnlyList<KernelEffect> Effects { get; }

    public IReadOnlyList<LeaseCommandResult> CompletedCommands { get; }

    public bool ProtectionChanged { get; }
}
