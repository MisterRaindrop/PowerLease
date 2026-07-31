namespace PowerLease.Domain;

/// <summary>A set of weekdays, stored as a bit mask so it compares and persists as one value.</summary>
public readonly record struct DayOfWeekSet
{
    private const byte AllDaysMask = 0b111_1111;

    private readonly byte _mask;

    private DayOfWeekSet(byte mask) => _mask = mask;

    public static DayOfWeekSet Empty => new(0);

    public static DayOfWeekSet All => new(AllDaysMask);

    public static DayOfWeekSet Weekdays =>
        Of(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday);

    public static DayOfWeekSet Weekends => Of(DayOfWeek.Saturday, DayOfWeek.Sunday);

    public static DayOfWeekSet Of(params DayOfWeek[] days)
    {
        ArgumentNullException.ThrowIfNull(days);

        byte mask = 0;
        foreach (var day in days)
        {
            mask |= Bit(day);
        }

        return new DayOfWeekSet(mask);
    }

    public bool IsEmpty => _mask == 0;

    public int Count => System.Numerics.BitOperations.PopCount(_mask);

    public bool Contains(DayOfWeek day) => (_mask & Bit(day)) != 0;

    public DayOfWeekSet With(DayOfWeek day) => new((byte)(_mask | Bit(day)));

    public DayOfWeekSet Without(DayOfWeek day) => new((byte)(_mask & ~Bit(day)));

    /// <summary>The days in this set, Sunday first.</summary>
    public IEnumerable<DayOfWeek> Days
    {
        get
        {
            for (var day = DayOfWeek.Sunday; day <= DayOfWeek.Saturday; day++)
            {
                if (Contains(day))
                {
                    yield return day;
                }
            }
        }
    }

    public override string ToString() => IsEmpty ? "none" : string.Join(",", Days);

    private static byte Bit(DayOfWeek day)
    {
        if (day < DayOfWeek.Sunday || day > DayOfWeek.Saturday)
        {
            throw new ArgumentOutOfRangeException(nameof(day), day, "Not a day of the week.");
        }

        return (byte)(1 << (int)day);
    }
}
