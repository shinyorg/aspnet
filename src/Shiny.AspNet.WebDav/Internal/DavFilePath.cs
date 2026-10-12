namespace Shiny.AspNet.WebDav.Internal;

/// <summary>Turning a request path into something safe to hand to a file system.</summary>
static class DavFilePath
{
    /// <summary>
    /// Path comparison follows the platform, because that is what the file system will do. Treating
    /// a case-insensitive file system as case-sensitive is how a containment check passes while the
    /// open succeeds on a different file.
    /// </summary>
    public static readonly StringComparison PathComparison = OperatingSystem.IsLinux()
        ? StringComparison.Ordinal
        : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Validates a decoded request path and returns it as a relative platform path.
    /// <para>
    /// The path arrives already percent-decoded, so <c>%2e%2e%2f</c> is plain <c>../</c> by now —
    /// which is the whole reason the segment check happens here and not on the raw target.
    /// </para>
    /// </summary>
    public static bool TryNormalize(string requestPath, bool serveHiddenFiles, out string relativePath)
    {
        relativePath = string.Empty;

        if (requestPath.Length == 0)
            return false;

        // A null byte truncates the name for some file APIs, making the checked path and the opened
        // path two different things.
        if (requestPath.Contains('\0'))
            return false;

        var builder = new List<string>();

        foreach (var segment in requestPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;

            if (segment == "..")
                return false;

            if (!serveHiddenFiles && segment.StartsWith('.'))
                return false;

            // A backslash is a separator on Windows, so a segment containing one would smuggle a
            // directory change past the split above.
            if (segment.Contains('\\') || segment.Contains(':'))
                return false;

            if (segment.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;

            builder.Add(segment);
        }

        if (builder.Count == 0)
            return false;

        relativePath = string.Join(Path.DirectorySeparatorChar, builder);
        return true;
    }
}
