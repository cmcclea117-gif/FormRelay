namespace FormRelay.Api;

/// <summary>
/// Web-address style names ("service-request"): lowercase letters and digits, joined by single dashes.
/// Checked with a plain loop instead of a regex: the input comes from users, the length is capped before
/// anything else runs, and there is nothing to backtrack.
/// </summary>
public static class Slugs
{
    public const int MaxLength = 100;

    public static bool IsValid(string? s)
    {
        if (s is null || s.Length is 0 or > MaxLength) return false;
        var previousWasDash = true;   // true at the start, so a leading dash is rejected
        foreach (var c in s)
        {
            if (c == '-')
            {
                if (previousWasDash) return false;   // leading or doubled dash
                previousWasDash = true;
            }
            else if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9')) previousWasDash = false;
            else return false;
        }
        return !previousWasDash;   // a trailing dash is rejected
    }
}
