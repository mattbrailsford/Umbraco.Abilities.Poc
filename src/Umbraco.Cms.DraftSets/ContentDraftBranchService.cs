using System.Collections.Concurrent;

using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;

namespace Umbraco.Cms.DraftSets;

/// <summary>A named draft of one document, held apart from its default draft until it is promoted.</summary>
public sealed class ContentDraftBranch
{
    public Guid Key { get; } = Guid.NewGuid();

    public required Guid ContentKey { get; init; }

    public required string Name { get; init; }

    /// <summary>The document as it was when the branch was made. Saves to the default draft never change it.</summary>
    internal DraftContent Base { get; init; } = null!;

    /// <summary>The branch's own copy of the whole document.</summary>
    internal DraftContent Content { get; set; } = null!;
}

/// <summary>A branch is about to be promoted. Cancel it to refuse; nothing is saved.</summary>
public sealed class ContentDraftBranchPromotingNotification(ContentDraftBranch branch, EventMessages messages) : CancelableNotification(messages)
{
    /// <summary>The branch being promoted. (In PR #24784 this is the protected <c>Target</c>: a handler can't see it.)</summary>
    public ContentDraftBranch Branch { get; } = branch;
}

public sealed class ContentDraftBranchPromotedNotification(ContentDraftBranch branch) : INotification
{
    public ContentDraftBranch Branch { get; } = branch;
}

/// <summary>
/// A stand-in for the named draft branches of umbraco/Umbraco-CMS PR #24784, built as if the requirement tests of
/// PR #24806 pass. It is not that PR's code (which targets Umbraco 20); it fakes the behaviour we asked for, so the
/// change set can be tried on branches:
/// <list type="bullet">
///   <item>Saving a branch changes nothing outside it: not the node, its name in the tree, its parent, its edited flag
///   or its history, and no content notifications fire.</item>
///   <item>The branch keeps the version it started from (its base), whatever is saved to the default draft meanwhile.</item>
///   <item>Promoting merges: the values the branch changed since its base are saved over the default draft, so changes
///   made there to other values are kept. Where both changed a value, the branch wins.</item>
///   <item>Promoting joins the caller's scope: if the scope rolls back, nothing is saved and the branch is kept.</item>
///   <item>Abandoning a branch leaves no trace in the document's history.</item>
/// </list>
/// Two differences from the PR, to fit Umbraco 18: content is in editor format (the PR hands out <c>IContent</c>,
/// which the PR's <c>MapUpdateAsync</c> fills), and branches are kept in memory, not in the database.
/// </summary>
public sealed class ContentDraftBranchService
{
    private readonly EditorContent _content;
    private readonly ICoreScopeProvider _scopes;
    private readonly IEventAggregator _events;
    private readonly ConcurrentDictionary<Guid, ContentDraftBranch> _branches = new();

    public ContentDraftBranchService(
        IContentEditingService editing,
        PropertyEditorCollection editors,
        IJsonSerializer json,
        ICoreScopeProvider scopes,
        IEventAggregator events,
        IIdKeyMap idKeys)
    {
        _content = new EditorContent(editing, editors, json, idKeys);
        _scopes = scopes;
        _events = events;
    }

    /// <summary>Makes a branch of the document as it is now; null if there is no such document.</summary>
    public async Task<ContentDraftBranch?> CreateAsync(Guid contentKey, string name)
    {
        if (await _content.GetLiveAsync(Udi(contentKey)) is not { } live)
        {
            return null;
        }

        var branch = new ContentDraftBranch { ContentKey = contentKey, Name = name, Base = live, Content = live.Clone() };
        _branches[branch.Key] = branch;
        return branch;
    }

    public ContentDraftBranch? GetByKey(Guid contentKey, Guid branchKey)
        => _branches.TryGetValue(branchKey, out var branch) && branch.ContentKey == contentKey ? branch : null;

    /// <summary>The branch's copy of the document.</summary>
    public DraftContent? GetContent(Guid contentKey, Guid branchKey) => GetByKey(contentKey, branchKey)?.Content.Clone();

    /// <summary>The document as it was when the branch was made, for a three-way compare with the branch and the default draft.</summary>
    public DraftContent? GetBase(Guid contentKey, Guid branchKey) => GetByKey(contentKey, branchKey)?.Base.Clone();

    /// <summary>Saves the branch's copy. Nothing outside the branch changes. Its parent is ignored: a branch doesn't move.</summary>
    public bool Save(Guid contentKey, Guid branchKey, DraftContent content)
    {
        if (GetByKey(contentKey, branchKey) is not { } branch)
        {
            return false;
        }

        var copy = content.Clone();
        copy.ParentKey = branch.Base.ParentKey;
        branch.Content = copy;
        return true;
    }

    /// <summary>
    /// Saves the values the branch changed since its base over the default draft, then removes the branch. Returns
    /// false if a handler cancelled it. Throws if Umbraco reports a problem, for the caller's scope to roll back.
    /// </summary>
    public async Task<bool> PromoteAsync(Guid contentKey, Guid branchKey, Guid userKey)
    {
        var branch = GetByKey(contentKey, branchKey) ?? throw new InvalidOperationException($"No branch {branchKey} of {contentKey}.");
        using var scope = _scopes.CreateCoreScope();
        var messages = new EventMessages();
        if (await _events.PublishCancelableAsync(new ContentDraftBranchPromotingNotification(branch, messages)))
        {
            return false; // Not completed: an outer scope can't complete either.
        }

        var live = await _content.GetLiveAsync(Udi(contentKey)) ?? throw new InvalidOperationException($"Document {contentKey} is gone.");
        foreach (var change in EditorContent.Diff(branch.Base, branch.Content).Where(c => c.Alias != DraftSetService.ParentAlias))
        {
            EditorContent.Overlay(live, change);
        }

        await _content.SaveAsync(live, userKey);
        scope.Notifications.Publish(new ContentDraftBranchPromotedNotification(branch));

        // Removed only once the outermost scope commits, so a rollback keeps it.
        _scopes.Context!.Enlist($"draft-branch-promoted:{branch.Key}", committed =>
        {
            if (committed)
            {
                _branches.TryRemove(branch.Key, out _);
            }
        });
        scope.Complete();
        return true;
    }

    /// <summary>Removes the branch. It never was a version of the document, so its history doesn't change.</summary>
    public void Abandon(Guid contentKey, Guid branchKey)
    {
        if (GetByKey(contentKey, branchKey) is not null)
        {
            _branches.TryRemove(branchKey, out _);
        }
    }

    private static GuidUdi Udi(Guid key) => new(Constants.UdiEntityType.Document, key);
}
