namespace BlueBillywig.Umbraco.Configuration;

/// <summary>
/// The one definition of a valid publication name, shared by the settings dashboard, the settings store and every render
/// path, so a value one accepts is never refused by another (a dotted slug such as <c>bb.dev</c> is a real publication on a
/// development OVP). It is the reference plugin's shape: lowercase ASCII letters, digits, dots and dashes; starts and ends
/// with a letter or digit; no two dots in a row, because <c>https://bb..dev.bbvms.com</c> silently fails to resolve; at most
/// 63 characters. Because only <c>[a-z0-9.-]</c> passes, a valid value is safe inside a URL host.
/// <para>
/// The minimum is 2, not the reference plugin's 3: the development publication is <c>bb</c>, and it is configured, pasted as
/// <c>bb.bbvms.com</c> URLs and stored in property values. What someone types into the settings dashboard still needs 3
/// (<see cref="MinTypedLength"/>, the reference plugin's rule); a value that is configured or already stored is held only to
/// this shape.
/// </para>
/// </summary>
public static class PublicationSlug
{
    /// <summary>The reference plugin's minimum, applied to what is typed into the settings dashboard.</summary>
    public const int MinTypedLength = 3;

    public static bool IsValid(string? publication) =>
        publication is { Length: >= 2 and <= 63 }
        && IsEdge(publication[0]) && IsEdge(publication[^1])
        && publication.All(c => IsEdge(c) || c is '.' or '-')
        && !publication.Contains("..", StringComparison.Ordinal);

    private static bool IsEdge(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
}
