namespace PowerLease.Domain;

/// <summary>Lifecycle of a keep-awake lease.</summary>
public enum LeaseStatus
{
    /// <summary>Still holding the machine awake.</summary>
    Active,

    /// <summary>Ran to the end of its duration.</summary>
    Expired,

    /// <summary>Ended before its duration ran out, by request.</summary>
    Released
}
