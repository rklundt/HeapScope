using System.Globalization;

namespace HeapScope.Application.Snapshots;

/// <summary>
/// A growth limit for <c>diff --fail-on-growth</c> (sprint 1.4): a size in bytes, or a percent of the matching rows'
/// baseline bytes. Growth exactly at the limit passes. A percent of a zero baseline allows no growth at all: anything
/// that grows from nothing exceeds every percentage.
/// </summary>
public readonly record struct GrowthThreshold(long? Bytes, double? Percent)
{
    /// <summary>The largest allowed growth in bytes for a baseline of <paramref name="baselineBytes"/>.</summary>
    public long LimitFor(long baselineBytes)
    {
        if (Bytes is { } bytes)
        {
            return bytes;
        }

        var limit = Math.Floor(Math.Max(0, baselineBytes) * Percent!.Value / 100.0);
        return limit >= long.MaxValue ? long.MaxValue : (long)limit;
    }

    /// <summary>Whether <paramref name="bytesDelta"/> grew past the limit for <paramref name="baselineBytes"/>.</summary>
    public bool IsExceededBy(long bytesDelta, long baselineBytes) => bytesDelta > LimitFor(baselineBytes);

    /// <summary>
    /// Parses <c>10%</c>, <c>50MB</c>, <c>512KB</c>, <c>1.5GB</c>, <c>200B</c> or a plain byte count (units are 1024-based
    /// and ignore case; spaces around the number are allowed). Negative, non-finite and out-of-range values are refused.
    /// </summary>
    public static bool TryParse(string? text, out GrowthThreshold threshold)
    {
        threshold = default;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.EndsWith('%'))
        {
            if (TryParseNumber(value[..^1], out var percent))
            {
                threshold = new GrowthThreshold(null, percent);
                return true;
            }

            return false;
        }

        (string Suffix, long Factor)[] units = [("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10), ("B", 1)];
        long factor = 1;
        foreach (var (suffix, unit) in units)
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                factor = unit;
                value = value[..^suffix.Length];
                break;
            }
        }

        if (TryParseNumber(value, out var amount) && amount * factor < long.MaxValue)
        {
            threshold = new GrowthThreshold((long)(amount * factor), null);
            return true;
        }

        return false;
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);
}
