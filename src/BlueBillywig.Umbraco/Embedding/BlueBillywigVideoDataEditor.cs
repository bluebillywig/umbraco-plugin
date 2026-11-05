using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.PropertyEditors;

namespace BlueBillywig.Umbraco.Embedding;

/// <summary>Server side of the "Blue Billywig Video" property editor schema: a JSON value, edited by the picker UI.</summary>
[DataEditor(Constants.VideoEditorAlias, ValueType = ValueTypes.Json, ValueEditorIsReusable = true)]
public sealed class BlueBillywigVideoDataEditor : DataEditor
{
    public BlueBillywigVideoDataEditor(IDataValueEditorFactory dataValueEditorFactory)
        : base(dataValueEditorFactory)
    {
    }
}
