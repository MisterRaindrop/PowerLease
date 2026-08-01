using PowerLease.Application.Kernel;

namespace PowerLease.Application.Hosting;

/// <summary>
/// Carries out the durable work the kernel asks for, off the kernel's thread.
/// <para>
/// Implementations do the blocking part: writing a lease and the record that its command was carried out in
/// one transaction, or appending to the protection history. They may take as long as a disk takes; the loop
/// that drives them is not the loop that decides whether to keep the machine awake.
/// </para>
/// <para>
/// An implementation must not throw. The loop catches anything that escapes and turns it into a failure, so
/// throwing costs nothing but the chance to say something useful about what went wrong.
/// </para>
/// </summary>
public interface IEffectExecutor
{
    Task<EffectCompletion> ExecuteAsync(KernelEffect effect, CancellationToken cancellationToken);
}
