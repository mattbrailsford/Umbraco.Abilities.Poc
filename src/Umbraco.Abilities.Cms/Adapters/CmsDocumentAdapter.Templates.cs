using Umbraco.Abilities.Entities;

namespace Umbraco.Abilities.Cms;

// Document blueprints as templates: a new document can start from one, as "Create from blueprint" does in the
// backoffice. Media and members have no blueprints in Umbraco, so they have no templates.
public sealed partial class CmsDocumentAdapter
{
    // Enough for any one type: blueprints are a few starting points, not content.
    private const int MaxBlueprintsPerType = 500;

    public async Task<IReadOnlyList<EntityTemplate>> GetTemplatesAsync(string? subtype, CancellationToken cancellationToken = default)
    {
        var types = subtype is null ? ListTypes() : FindType(subtype) is { } type ? [type] : [];
        var templates = new List<EntityTemplate>();
        foreach (var contentType in types)
        {
            var page = await blueprints.GetPagedByContentTypeAsync(contentType.Key, 0, MaxBlueprintsPerType);
            templates.AddRange((page.Result?.Items ?? []).Select(b => new EntityTemplate(b.Key, b.Name ?? b.Key.ToString(), contentType.Alias)));
        }

        return templates;
    }

    // Umbraco's scaffold is the blueprint as a new document's starting values, as the backoffice creates from one.
    public async Task<Entity?> NewFromTemplateAsync(Guid key, Guid template, CancellationToken cancellationToken = default)
        => await blueprints.GetScaffoldedAsync(template) is { } scaffold ? await NewFromAsync(key, scaffold, cancellationToken) : null;
}
