using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace BlueBillywig.Umbraco.Embedding;

/// <summary>
/// Turns the references stored in rich text into embeds, when the page is rendered.
/// <para>
/// The rich text editor stores what the oEmbed provider returned inside Umbraco's own
/// <c>umb-embed-holder</c> element, whose <c>data-embed-url</c> is the pasted URL. The whole holder is replaced by
/// the embed: the stored value is a reference, and the markup (consent, nonce, assets, poster) is made fresh for
/// every request. Content saved before this existed has the player and poster baked in, and the editor's own HTML
/// parser can pull that markup out of the holder (a block inside a span inside a paragraph) when the content is
/// opened and saved again. Such left-over markup, recognised by the class names this package's renderer gives its
/// wrappers (and the oEmbed provider's own reference text), is removed, so no baked player or poster outlives the
/// consent decision.
/// </para>
/// <para>
/// Blocks in rich text leave a <c>bb-rte-video-ref</c> element for the same reason: block partials run when the
/// value is converted, and that result is cached across requests.
/// </para>
/// <para>
/// Rich text is untrusted too: an editor, an import or an older version may have stored markup that speaks the package's
/// vocabulary (the lazy loader's wrapper, the form overlay's <c>bb-cta-form</c>, the consent button). The content is parsed
/// with HtmlAgilityPack (already in the dependency closure through Umbraco, which uses it for rich text itself), so any tag,
/// any case, quoted or unquoted attributes and entity-encoded class names are seen as the browser will see them. Every
/// element that is not a reference loses its <c>data-bb-*</c> attributes and its <c>bb-*</c> class tokens; embeds are
/// inserted afterwards, so the package's own output is never touched. This also holds for the Delivery API form.
/// </para>
/// </summary>
public static partial class RichTextEmbedRewriter
{
    public const string HolderClass = "umb-embed-holder";
    public const string BlockRefClass = "bb-rte-video-ref";

    /// <summary>
    /// True when the markup may hold something to rewrite or to neutralise: a holder, a reference, or anything in the package's
    /// vocabulary (<c>bb-</c> in a class or <c>data-bb-</c> attribute, in any case, or an entity inside an attribute value that
    /// could spell it). A cheap test, so other rich text is left alone.
    /// </summary>
    public static bool MayContainEmbeds(string? html)
    {
        if (html is null)
        {
            return false;
        }

        if (html.Contains(HolderClass, StringComparison.OrdinalIgnoreCase) || html.Contains("bb-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return EntityInAttribute().IsMatch(html);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    /// <summary>
    /// The Delivery API form: the references stay, with what is inside them emptied and <c>data-embed-url</c> kept, so a
    /// headless client sees the reference and never player markup that an older editor saved.
    /// </summary>
    public static string ToReferences(string html) =>
        RewriteAsync(html, _ => Task.FromResult(""), keepEmbedUrl: true).GetAwaiter().GetResult();

    /// <param name="html">The stored rich text.</param>
    /// <param name="render">Makes the embed for one reference.</param>
    /// <param name="keepEmbedUrl">The headless form: keep the (emptied) reference element.</param>
    /// <param name="oembedDefaults">
    /// Adjusts a video parsed from a pasted URL (the oEmbed reference) before it is rendered. Used for the lazy-load
    /// switches; a block reference stores its own choices and is never adjusted.
    /// </param>
    public static async Task<string> RewriteAsync(string html, Func<BlueBillywigVideo, Task<string>> render, bool keepEmbedUrl = false, Func<BlueBillywigVideo, BlueBillywigVideo>? oembedDefaults = null)
    {
        if (!MayContainEmbeds(html))
        {
            return html;
        }

        var doc = new HtmlDocument { OptionMaxNestedChildNodes = 200 };
        try
        {
            doc.LoadHtml(html);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Pathological nesting. Fail closed: nothing of content that cannot be inspected is printed.
            return "";
        }

        // Embeds are put in as markers first and swapped for the rendered markup at the end, so the cleaning above never
        // sees the renderer's own output.
        string marker = "bbph" + Guid.NewGuid().ToString("N") + "-";
        var rendered = new List<string>();
        bool changed = false;

        var pending = new Stack<HtmlNode>();
        pending.Push(doc.DocumentNode);
        while (pending.Count > 0)
        {
            HtmlNode parent = pending.Pop();
            foreach (HtmlNode node in parent.ChildNodes.ToList())
            {
                if (node.NodeType != HtmlNodeType.Element)
                {
                    continue;
                }

                Kind kind = Classify(node, out BlueBillywigVideo? video, out bool fromUrl);

                // Without an end tag of its own the extent of an element is a guess of the parser (everything up to the end of
                // its parent). A reference like that is left as it is, as before; baked markup like that is neutralised below.
                if (kind == Kind.Reference && !IsClosed(node))
                {
                    kind = Kind.None;
                }

                switch (kind)
                {
                    case Kind.Reference when keepEmbedUrl:
                        // Headless output: keep the reference element, emptied. Its own attributes are the reference.
                        if (node.HasChildNodes)
                        {
                            node.RemoveAllChildren();
                            changed = true;
                        }

                        continue;
                    case Kind.Reference:
                        if (fromUrl && video is not null && oembedDefaults is not null)
                        {
                            video = oembedDefaults(video);
                        }

                        // The embed replaces the holder; the holder's own attributes were only for the backoffice editor.
                        rendered.Add(await render(video!));
                        parent.ReplaceChild(HtmlNode.CreateNode(marker + (rendered.Count - 1) + ";"), node);
                        changed = true;
                        continue;
                    case Kind.Baked when IsClosed(node):
                        // Left-over markup of an earlier version: gone, with everything inside it.
                        parent.RemoveChild(node);
                        changed = true;
                        continue;
                }

                // An ordinary element, or baked markup that never closes (it cannot be removed without taking what follows):
                // it keeps its place but not the package's vocabulary.
                changed |= Neutralise(node);
                pending.Push(node);
            }
        }

        if (!changed)
        {
            return html;
        }

        string output = doc.DocumentNode.OuterHtml;
        for (int i = 0; i < rendered.Count; i++)
        {
            output = output.Replace(marker + i + ";", rendered[i], StringComparison.Ordinal);
        }

        return output;
    }

    private enum Kind
    {
        None,
        Reference,
        Baked,
    }

    // Class names this package's renderer puts on its wrappers. Rich text written by an editor never has them.
    private static readonly string[] BakedClasses = ["bb-block-embed", "bb-channel-embed", "bb-consent-placeholder", "bb-lazy-embed", "bb-oembed-ref"];

    private static readonly char[] AsciiWhitespace = [' ', '\t', '\n', '\r', '\f'];

    /// <summary>What an element is: a reference to render, left-over baked markup, or neither.</summary>
    private static Kind Classify(HtmlNode node, out BlueBillywigVideo? video, out bool fromUrl)
    {
        video = null;
        fromUrl = false;
        string[] tokens = ClassTokens(node);
        if (tokens.Length == 0)
        {
            return Kind.None;
        }

        bool Has(string name) => tokens.Contains(name, StringComparer.OrdinalIgnoreCase);

        if (Has(HolderClass))
        {
            // Only URLs the oEmbed provider accepts. The stored value is data, and anything else stays as it was.
            video = BlueBillywigUrls.TryParseOEmbedUrl(Attribute(node, "data-embed-url"));
            fromUrl = true;
            return video is null ? Kind.None : Kind.Reference;
        }

        if (Has(BlockRefClass))
        {
            video = BlueBillywigVideo.Parse(Attribute(node, "data-bb-video"));
            return video is null ? Kind.None : Kind.Reference;
        }

        return BakedClasses.Any(Has) ? Kind.Baked : Kind.None;
    }

    /// <summary>All class tokens of the element as a browser reads them: entities decoded, split on ASCII whitespace.</summary>
    private static string[] ClassTokens(HtmlNode node) =>
        node.Attributes.AttributesWithName("class")
            .SelectMany(a => WebUtility.HtmlDecode(a.Value ?? "").Split(AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

    private static string? Attribute(HtmlNode node, string name)
    {
        HtmlAttribute? attribute = node.Attributes.AttributesWithName(name).FirstOrDefault();
        return attribute is null ? null : WebUtility.HtmlDecode(attribute.Value ?? "");
    }

    /// <summary>True when the element has an end tag of its own (not one the parser made up at the end of its parent).</summary>
    private static bool IsClosed(HtmlNode node) =>
        node.EndNode is { } end && !ReferenceEquals(end, node) && end.OuterStartIndex >= 0;

    /// <summary>Removes <c>data-bb-*</c> attributes and <c>bb-*</c> class tokens. True when something was removed.</summary>
    private static bool Neutralise(HtmlNode node)
    {
        bool changed = false;
        foreach (HtmlAttribute attribute in node.Attributes.ToList())
        {
            if (EmbedHtml.IsPackageAttribute(attribute.Name))
            {
                attribute.Remove();
                changed = true;
            }
            else if (attribute.Name.Equals("class", StringComparison.OrdinalIgnoreCase))
            {
                string decoded = WebUtility.HtmlDecode(attribute.Value ?? "");
                if (!HasPackageToken(decoded))
                {
                    continue;
                }

                string stripped = EmbedHtml.StripPackageClasses(decoded);
                if (stripped.Length == 0)
                {
                    attribute.Remove();
                }
                else
                {
                    attribute.Value = WebUtility.HtmlEncode(stripped);
                }

                changed = true;
            }
        }

        return changed;
    }

    private static bool HasPackageToken(string classes) =>
        classes.Split(AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries).Any(t => t.StartsWith("bb-", StringComparison.OrdinalIgnoreCase));

    // A class or data-* attribute whose value holds an entity: the browser decodes it before it matches a class name.
    [GeneratedRegex(@"\b(?:class|data-[a-z0-9_.:-]*)\s*=\s*(?:""[^""]*&|'[^']*&|[^\s""'>]*&)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EntityInAttribute();
}
