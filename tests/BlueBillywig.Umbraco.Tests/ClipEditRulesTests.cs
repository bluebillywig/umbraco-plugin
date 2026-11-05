using BlueBillywig.Umbraco.Editing;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>Ports of the plugin's UpdateClipMetadataAllowlistTest and the validation in <c>bulk_action</c> and <c>update_clip_status</c>.</summary>
public class ClipEditRulesTests
{
    [Theory]
    [InlineData("published", true)]
    [InlineData("draft", true)]
    [InlineData("archived", true)]
    [InlineData("weird", false)]
    [InlineData("Published", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Status_is_validated_against_the_allowlist(string? value, bool valid) => Assert.Equal(valid, ClipEditRules.IsStatus(value));

    [Theory]
    [InlineData("545179f0-1503-46d6-9cc7-041fab63fc48", true)]
    [InlineData("545179F0-1503-46D6-9CC7-041FAB63FC48", true)]
    [InlineData("70", false)]
    [InlineData("not-a-uuid", false)]
    [InlineData("545179f0-1503-46d6-9cc7-041fab63fc48 ", false)]
    [InlineData("545179f0-1503-46d6-9cc7-041fab63fc4g", false)]
    [InlineData("", false)]
    public void Cpp_is_validated_as_a_uuid(string value, bool valid) => Assert.Equal(valid, ClipEditRules.IsCppCode(value));

    [Theory]
    [InlineData("", true)]
    [InlineData("2030-01-01", true)]
    [InlineData("2030-01-01T10:00", true)]
    [InlineData("2030-01-01 10:00:00", true)]
    [InlineData("2030-01-01T10:00:00Z", true)]
    [InlineData("2030-01-01T10:00:00+02:00", true)]
    [InlineData("2030-01-01T10:00:00-0500", true)]
    [InlineData("tomorrow", false)]
    [InlineData("2030-01-01T10", false)]
    [InlineData("2030-01-01'; drop", false)]
    public void Embargo_dates_follow_the_plugins_format(string value, bool valid) => Assert.Equal(valid, ClipEditRules.IsEmbargo(value));

    [Theory]
    [InlineData("abc", true)]
    [InlineData("A1_b-2", true)]
    [InlineData("../../sapi/admintoken", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void Folder_codes_and_playouts_cannot_carry_paths(string value, bool valid)
    {
        Assert.Equal(valid, ClipEditRules.IsFolderCode(value));
        Assert.Equal(valid, ClipEditRules.IsPlayout(value));
    }

    [Theory]
    [InlineData("Colour", "colour")]
    [InlineData("related_playout", "related_playout")]
    [InlineData("a b!c", "abc")]
    [InlineData("status;", "status")]
    public void Custom_field_keys_are_sanitised_like_sanitize_key(string name, string key) => Assert.Equal(key, ClipEditRules.SanitizeKey(name));

    [Fact]
    public void Bulk_ids_are_distinct_and_valid_before_the_cap_is_counted()
    {
        // The plugin: absint, drop zeros, unique, then count. Fifty distinct ids pass, fifty-one do not.
        Assert.True(ClipEditRules.TryParseBulkIds(Enumerable.Range(1, 50).Select(i => (long)i), out List<long> ids, out _));
        Assert.Equal(50, ids.Count);
        Assert.False(ClipEditRules.TryParseBulkIds(Enumerable.Range(1, 51).Select(i => (long)i), out _, out string error));
        Assert.Equal("Please process in batches of 50 or fewer.", error);

        Assert.True(ClipEditRules.TryParseBulkIds(Enumerable.Repeat(7L, 80).Concat([0, -3, 9]), out ids, out _));
        Assert.Equal([7L, 9L], ids);
    }

    [Fact]
    public void Bulk_without_usable_ids_is_refused()
    {
        Assert.False(ClipEditRules.TryParseBulkIds(null, out _, out string error));
        Assert.Equal("No clip IDs provided", error);
        Assert.False(ClipEditRules.TryParseBulkIds([0, -1, ClipEditRules.MaxId], out _, out _));
    }

    [Theory]
    [InlineData("status", "draft", true)]
    [InlineData("status", "", false)]
    [InlineData("status", "weird", false)]
    [InlineData("cpp", "", true)]
    [InlineData("cpp", "545179f0-1503-46d6-9cc7-041fab63fc48", true)]
    [InlineData("cpp", "70", false)]
    [InlineData("folder", "", true)]
    [InlineData("folder", "abc_1", true)]
    [InlineData("folder", "a/b", false)]
    [InlineData("delete", "", false)]
    [InlineData("whatever", "x", false)]
    public void Bulk_values_are_validated_per_action(string action, string value, bool valid) =>
        Assert.Equal(valid, ClipEditRules.TryParseBulkValue(action, value, out _, out _));
}
