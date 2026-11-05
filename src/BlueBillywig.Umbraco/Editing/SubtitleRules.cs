using System.Text;
using System.Text.RegularExpressions;

namespace BlueBillywig.Umbraco.Editing;

/// <summary>Subtitle upload checks, ported from the plugin's <c>upload_subtitle</c> and <c>looks_like_subtitle</c>.</summary>
public static partial class SubtitleRules
{
    /// <summary>The plugin's limit: 1 MB, measured on the contents, not on what the browser claims.</summary>
    public const int MaxBytes = 1024 * 1024;

    public static readonly string[] Extensions = ["vtt", "srt"];

    /// <summary>The extension of an accepted file name (lower case, no dot), or null when it is neither .vtt nor .srt.</summary>
    public static string? Extension(string? fileName)
    {
        string leaf = (fileName ?? "").Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
        int dot = leaf.LastIndexOf('.');
        string ext = dot >= 0 ? leaf[(dot + 1)..].ToLowerInvariant() : "";
        return Extensions.Contains(ext) ? ext : null;
    }

    /// <summary>
    /// Whether the text starts like the format its extension claims: <c>WEBVTT</c> for .vtt, a cue number and an
    /// <c>hh:mm:ss,mmm --&gt; hh:mm:ss,mmm</c> line for .srt. A BOM and leading blank space are ignored.
    /// </summary>
    public static bool LooksLikeSubtitle(string contents, string ext)
    {
        string head = contents.Length > 1024 ? contents[..1024] : contents;
        head = head.TrimStart('﻿', ' ', '\t', '\r', '\n');
        return ext == "vtt" ? head.StartsWith("WEBVTT", StringComparison.Ordinal) : SrtStart().IsMatch(head);
    }

    public static int ByteCount(string contents) => Encoding.UTF8.GetByteCount(contents);

    [GeneratedRegex(@"^\s*\d+\s*\r?\n\d{2}:\d{2}:\d{2},\d{3}\s*-->\s*\d{2}:\d{2}:\d{2},\d{3}")]
    private static partial Regex SrtStart();
}
