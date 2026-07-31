using PowerLease.Domain;

namespace PowerLease.Application.Kernel;

/// <summary>
/// Something that changes what the kernel believes, as opposed to a measurement.
/// <para>
/// These travel on their own channel and are never dropped. A measurement that is lost costs
/// resolution; a resume notification that is lost means the kernel goes on trusting samples taken
/// before the machine slept.
/// </para>
/// </summary>
public abstract record ControlMessage;

/// <summary>
/// The machine has woken up.
/// <para>
/// Everything observed before this is meaningless, and the operating system dropped the power request
/// when the machine suspended, so it has to be taken out again and confirmed.
/// </para>
/// </summary>
public sealed record ResumedFromSleep(string Reason) : ControlMessage;

/// <summary>
/// The service has started, possibly after a crash.
/// <para>
/// The power request died with the process, so the machine was unprotected for however long the restart
/// took, and no source has reported yet.
/// </para>
/// </summary>
public sealed record ServiceStarted(string Reason) : ControlMessage;

/// <summary>
/// Configuration has been replaced. Results computed under the old one are no longer admissible.
/// </summary>
public sealed record ConfigurationReplaced(long ConfigGeneration) : ControlMessage;

/// <summary>
/// The time zone or the system clock changed.
/// <para>
/// Every source is asked to report again rather than the kernel recomputing anything itself. A schedule
/// is written in local time, so a source that evaluated one against the old zone may now be wrong, and
/// the kernel has no way to tell which sources those are.
/// </para>
/// </summary>
public sealed record TimeAdjusted(string Reason) : ControlMessage;

/// <summary>A failure worth latching. While it is latched the machine stays awake.</summary>
public sealed record FaultObserved(string Key, FaultSeverity Severity, string Message) : ControlMessage;

/// <summary>A previously reported failure has gone away for one observation.</summary>
public sealed record FaultHealthy(string Key) : ControlMessage;

/// <summary>Something explicitly repaired a failure. The only way out for a persistent fault.</summary>
public sealed record FaultRepaired(string Key) : ControlMessage;

/// <summary>An effect the kernel asked for has finished.</summary>
public sealed record EffectFinished(EffectCompletion Completion) : ControlMessage;
