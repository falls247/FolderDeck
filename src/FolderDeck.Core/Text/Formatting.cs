using System.Globalization;

namespace FolderDeck.Core.Text;

public static class Formatting
{
    /// <summary>"たった今" / "N分前" / "N時間前" / "昨日" / "N日前" / "yyyy/MM/dd" (7 days or older).</summary>
    public static string RelativeTime(DateTimeOffset when, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var elapsed = now - when;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "たった今";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{(int)elapsed.TotalMinutes}分前";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{(int)elapsed.TotalHours}時間前";
        }

        int days = (int)elapsed.TotalDays;
        if (days == 1)
        {
            return "昨日";
        }

        if (days < 7)
        {
            return $"{days}日前";
        }

        return LocalTime(when, zone).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
    }

    /// <summary><c>yyyy/MM/dd HH:mm</c> in local time.</summary>
    public static string DateTimeText(DateTimeOffset when, TimeZoneInfo? zone = null) =>
        LocalTime(when, zone).ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"512 B", "1.2 KB", "34.5 MB", "120 GB".</summary>
    public static string FileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        string[] units = ["KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        int unit = -1;
        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < units.Length - 1);

        string number = value >= 100
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{number} {units[unit]}";
    }

    private static DateTimeOffset LocalTime(DateTimeOffset when, TimeZoneInfo? zone) =>
        TimeZoneInfo.ConvertTime(when, zone ?? TimeZoneInfo.Local);
}
