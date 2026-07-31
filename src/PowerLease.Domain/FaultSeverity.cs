namespace PowerLease.Domain;

/// <summary>How a fault is allowed to clear.</summary>
public enum FaultSeverity
{
    /// <summary>
    /// May clear itself once the source has been healthy for several consecutive observations. One
    /// good reading is not enough, because an intermittent failure alternates.
    /// </summary>
    Transient,

    /// <summary>
    /// Stays until something explicitly repairs it: corrupt configuration, a failed migration, or
    /// the system refusing to honour a power request. Nothing that merely looks healthy afterwards
    /// clears it, because the underlying damage has not been addressed.
    /// </summary>
    Persistent
}
