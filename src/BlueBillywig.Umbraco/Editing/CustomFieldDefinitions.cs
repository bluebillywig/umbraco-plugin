using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.ViewModels;

namespace BlueBillywig.Umbraco.Editing;

/// <summary>
/// The custom fields SAPI defines for clips, read from <c>/sapi/customfieldgroup</c>. They are both what the editor shows
/// (the plugin's <c>get_custom_field_groups</c>) and the allowlist for writing (<c>get_allowed_custom_field_keys</c>):
/// without the allowlist a custom-field write could carry <c>status</c>, <c>cpp</c> or <c>foldercode</c> and bypass their validation.
/// </summary>
public static class CustomFieldDefinitions
{
    /// <summary>
    /// Groups that apply to clips and have at least one field. SAPI may return <c>entity</c> as a single string or as
    /// an array (the plugin's fatal TypeError of 2.0.x), and the fields under <c>customFields.mediaclip</c> or, failing
    /// that, under the first entity key.
    /// </summary>
    public static IReadOnlyList<CustomFieldGroupResponseModel> ParseGroups(JsonNode? items)
    {
        var groups = new List<CustomFieldGroupResponseModel>();
        if (items is not JsonArray array)
        {
            return groups;
        }

        foreach (JsonObject g in array.OfType<JsonObject>())
        {
            if (!Entities(g["entity"]).Contains("mediaclip", StringComparer.Ordinal))
            {
                continue;
            }

            // SAPI is PHP behind the scenes: an empty customFields comes as [] and not {}, so it is not assumed to be an object.
            JsonObject? byEntity = g["customFields"] as JsonObject;
            JsonArray? fieldsNode = byEntity?["mediaclip"] as JsonArray;
            if (fieldsNode is null or { Count: 0 } && byEntity is not null)
            {
                fieldsNode = byEntity.Select(p => p.Value).OfType<JsonArray>().FirstOrDefault();
            }

            var fields = new List<CustomFieldResponseModel>();
            foreach (JsonObject f in (fieldsNode ?? []).OfType<JsonObject>())
            {
                string name = LibraryMapper.Str(f["name"]);
                if (name.Length == 0)
                {
                    continue;
                }

                string label = LibraryMapper.Str(f["label"]);
                string type = LibraryMapper.Str(f["type"]);
                string itype = LibraryMapper.Str(f["itype"]);
                fields.Add(new CustomFieldResponseModel
                {
                    Name = name,
                    Label = label.Length > 0 ? label : name,
                    Type = type.Length > 0 ? type : "string",
                    IType = itype.Length > 0 ? itype : "string",
                });
            }

            if (fields.Count == 0)
            {
                continue;
            }

            long id = LibraryMapper.Int(g["id"]);
            string title = LibraryMapper.Str(g["title"]);
            groups.Add(new CustomFieldGroupResponseModel { Id = id, Title = title.Length > 0 ? title : $"Group {id}", Fields = fields });
        }

        return groups;
    }

    /// <summary>Sanitised key to the field's real name. A field is written under its real name, which the plugin lower-cases.</summary>
    public static Dictionary<string, string> Allowlist(IEnumerable<CustomFieldGroupResponseModel> groups)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CustomFieldResponseModel field in groups.SelectMany(g => g.Fields))
        {
            string key = ClipEditRules.SanitizeKey(field.Name);
            if (key.Length > 0)
            {
                keys.TryAdd(key, field.Name);
            }
        }

        return keys;
    }

    private static IEnumerable<string> Entities(JsonNode? node) => node switch
    {
        JsonArray array => array.Select(LibraryMapper.Str),
        JsonValue => [LibraryMapper.Str(node)],
        _ => [],
    };
}
