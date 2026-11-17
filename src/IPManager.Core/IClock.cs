namespace IPManager.Core;

/// <summary>Abstraction over the system clock so time-based logic is testable.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
}

public static class ClockExtensions
{
    public static string ToLocalDisplay(this DateTime utc) =>
        utc.Kind == DateTimeKind.Utc ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : utc.ToString("yyyy-MM-dd HH:mm:ss");
}
