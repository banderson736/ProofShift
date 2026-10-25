namespace ProofShift.DemoContracts;

public static class SafeDemoRunnerDiagnostic
{
    private const string Prefix = "Demo scenario failed unexpectedly (";
    private const string LocationSeparator = " <- ";
    private const string Suffix = ". Exception details were withheld to protect configured connection values.";

    public static string Format(string exceptionTypeName, IEnumerable<string> methodLocations)
    {
        ArgumentNullException.ThrowIfNull(exceptionTypeName);
        ArgumentNullException.ThrowIfNull(methodLocations);

        var safeExceptionType = IsExceptionType(exceptionTypeName) ? exceptionTypeName : "Exception";

        var safeLocations = methodLocations
            .Where(location => !string.IsNullOrEmpty(location) && location.All(IsLocationCharacter))
            .Where(location => location.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        var locationText = safeLocations.Length == 0
            ? string.Empty
            : $" at {string.Join(LocationSeparator, safeLocations)}";
        return $"{Prefix}{safeExceptionType}){locationText}{Suffix}";
    }

    public static bool TryParseLine(string line, out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(line);
        diagnostic = string.Empty;
        if (line.Length == 0 || line.Contains('\r') || line.Contains('\n') ||
            !line.StartsWith(Prefix, StringComparison.Ordinal) ||
            !line.EndsWith(Suffix, StringComparison.Ordinal))
            return false;

        var body = line.AsSpan(Prefix.Length, line.Length - Prefix.Length - Suffix.Length);
        var closeParen = body.IndexOf(')');
        if (closeParen <= 0 || !IsExceptionType(body[..closeParen]))
            return false;

        if (closeParen == body.Length - 1)
        {
            diagnostic = line;
            return true;
        }

        var trailing = body[(closeParen + 1)..];
        if (trailing.IsEmpty)
        {
            diagnostic = line;
            return true;
        }

        const string locationPrefix = " at ";
        if (!trailing.StartsWith(locationPrefix, StringComparison.Ordinal))
            return false;
        var locations = trailing[locationPrefix.Length..].ToString().Split(LocationSeparator,
            StringSplitOptions.None);
        if (locations.Length is < 1 or > 3 || locations.Any(location =>
                location.Length == 0 || location.Any(character => !IsLocationCharacter(character))))
            return false;

        diagnostic = line;
        return true;
    }

    private static bool IsExceptionType(string value) => IsExceptionType(value.AsSpan());

    private static bool IsExceptionType(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || !char.IsAsciiLetter(value[0])) return false;
        for (var index = 1; index < value.Length; index++)
            if (!(char.IsAsciiLetterOrDigit(value[index]) || value[index] == '_')) return false;
        return true;
    }

    private static bool IsLocationCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '$' or '+' or '<' or '>' or '`';
}
