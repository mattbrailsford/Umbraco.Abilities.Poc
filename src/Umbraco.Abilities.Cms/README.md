# Umbraco.Abilities.Cms

The framework on a real Umbraco: documents and media as entity types, through the CMS's own editing services,
permissions and Draft Sets. This is what CMS core would ship.

## Read it in this order

1. `AbilitiesBuilderExtensions.cs`: what a site registers.
2. `Adapters/CmsDocumentAdapter.cs`: load and validate a document. Then its two other parts:
   - `CmsDocumentAdapter.Permissions.cs`: the backoffice's own permission checks;
   - `CmsDocumentAdapter.DraftSets.cs`: how a change set stages content into a CMS draft set.
3. `Adapters/CmsMediaAdapter.cs`: media, on the same base, with no drafts.
4. `PropertyEditors/PropertyEditorHandlers.cs`: the contract a property editor implements to be editable.

## Folders

| Folder | What's in it |
| --- | --- |
| `Adapters/` | The document and media adapters, and the base for any entity whose types are content types |
| `Content/` | Shared plumbing: a content type as a schema, stored values in and out, and Umbraco's errors as entity paths |
| `PropertyEditors/` | One handler per property editor: describe, read and write its value. Packages add their own |
| `Infrastructure/` | The CMS's transaction for a change set, and the middleware that reads the `Umb-Change-Set` header |
