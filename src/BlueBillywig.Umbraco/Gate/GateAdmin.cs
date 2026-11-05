using Umbraco.Cms.Core.Services;

namespace BlueBillywig.Umbraco.Gate;

/// <summary>The names of the site's member groups, for the settings dashboard's group picker.</summary>
public interface IGateMemberGroupSource
{
    Task<IReadOnlyList<string>> GetNamesAsync(CancellationToken ct);
}

internal sealed class UmbracoGateMemberGroupSource : IGateMemberGroupSource
{
    private readonly IMemberGroupService _groups;

    public UmbracoGateMemberGroupSource(IMemberGroupService groups) => _groups = groups;

    public async Task<IReadOnlyList<string>> GetNamesAsync(CancellationToken ct) =>
        (await _groups.GetAllAsync()).Select(g => g.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Order(StringComparer.OrdinalIgnoreCase).ToArray();
}
