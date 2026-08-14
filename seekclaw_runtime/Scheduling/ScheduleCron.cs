using Cronos;

namespace SeekClaw.Runtime.Scheduling;

/// <summary>Validates 5-field cron expressions and computes the next occurrence in local time.</summary>
public static class ScheduleCron
{
    /// <summary>Parses and validates a 5-field cron expression; throws <see cref="CronFormatException"/> when invalid.</summary>
    public static CronExpression Parse(string cron)
    {
        var trimmed = (cron ?? "").Trim();
        if (trimmed.Length == 0) throw new CronFormatException("Cron expression is required.");
        var fields = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            throw new CronFormatException(
                "Cron expression must use the 5-field format: minute hour day-of-month month day-of-week.");
        return CronExpression.Parse(trimmed, CronFormat.Standard);
    }

    /// <summary>Next occurrence strictly after <paramref name="after"/>, in the machine's local time zone.</summary>
    public static DateTimeOffset? NextOccurrence(string cron, DateTimeOffset after)
    {
        try
        {
            var expression = Parse(cron);
            var zone = TimeZoneInfo.Local;
            // Use the DateTimeOffset overload: the DateTime overload interprets the
            // instant's wall clock in the target zone, which shifts the result by the
            // UTC offset and can yield an occurrence in the past (tasks then never fire
            // or fire on every tick).
            var next = expression.GetNextOccurrence(after, zone);
            return next?.ToUniversalTime();
        }
        catch (CronFormatException)
        {
            return null;
        }
    }
}
