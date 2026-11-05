using System.Globalization;
using Umbraco.Cms.Core.Services;

namespace BlueBillywig.Umbraco.Editing;

/// <summary>
/// The playout chosen for a clip in the library. SAPI has no such field, so, like the reference plugin's
/// <c>blue-billywig-playout-video-status</c> option, it is kept on this side. The library's embed code and preview use it
/// when the caller names no playout.
/// </summary>
public interface IClipPlayoutStore
{
    string? Get(long clipId);

    /// <summary>Stores the playout, or forgets it for a null or empty value. The caller validates the name.</summary>
    void Set(long clipId, string? playout);
}

internal sealed class ClipPlayoutStore : IClipPlayoutStore
{
    private readonly IKeyValueService _keyValues;

    public ClipPlayoutStore(IKeyValueService keyValues) => _keyValues = keyValues;

    public string? Get(long clipId)
    {
        string? value = _keyValues.GetValue(Key(clipId));
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public void Set(long clipId, string? playout) => _keyValues.SetValue(Key(clipId), playout ?? "");

    private static string Key(long clipId) => "BlueBillywig.ClipPlayout." + clipId.ToString(CultureInfo.InvariantCulture);
}
