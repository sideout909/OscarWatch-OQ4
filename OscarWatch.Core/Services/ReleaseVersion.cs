namespace OscarWatch.Core.Services;

public static class ReleaseVersion
{
    public static Version? TryParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var trimmed = tag.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[1..];

        var plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
            trimmed = trimmed[..plus];

        var suffix = trimmed.IndexOf('-', StringComparison.Ordinal);
        if (suffix >= 0)
            trimmed = trimmed[..suffix];

        return Version.TryParse(trimmed, out var version) ? version : null;
    }

    public static Version? TryParseVersionString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        var plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
            trimmed = trimmed[..plus];

        var suffix = trimmed.IndexOf('-', StringComparison.Ordinal);
        if (suffix >= 0)
            trimmed = trimmed[..suffix];

        return Version.TryParse(trimmed, out var version) ? version : null;
    }

    public static bool IsNewer(Version latest, Version current) => latest > current;
}
