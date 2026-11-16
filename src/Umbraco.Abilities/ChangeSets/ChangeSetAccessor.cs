using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Cache;

namespace Umbraco.Abilities.ChangeSets;

/// <summary>
/// The change set that the current call works in, or null for none. It is ambient: whoever starts a call sets it
/// (a header middleware for HTTP, the AI adapter for a tool call, Automate for a step), and everything the call runs
/// reads it. Write abilities are then recorded into the set, and reads show the set's changes. No ability input
/// carries a change set id.
/// </summary>
public interface IChangeSetAccessor
{
    /// <summary>The change set the current call works in, or null when none is open.</summary>
    Guid? CurrentChangeSetId { get; }

    /// <summary>Makes a change set (or none) current until the returned scope is disposed, then restores the one before.</summary>
    IDisposable Use(Guid? changeSetId);
}

/// <summary>
/// The default <see cref="IChangeSetAccessor"/>, built like the CMS Draft Sets proof of concept's accessor and the CMS's
/// own context accessors: stored per request while one is in flight, so it never leaks between requests, and in an
/// <see cref="AsyncLocal{T}"/> otherwise, so it flows through async calls outside a request.
/// </summary>
public sealed class ChangeSetAccessor(IRequestCache requestCache)
    : HybridAccessorBase<ChangeSetAccessor.Holder>(requestCache), IChangeSetAccessor
{
    /// <summary>The request header an HTTP caller uses to name the change set its request works in.</summary>
    public const string HeaderName = "Umb-Change-Set";

    /// <summary>An accessor with no request cache: the current set flows with the async call only. For tests and tools.</summary>
    public static ChangeSetAccessor Ambient() => new(NoAppCache.Instance);

    public Guid? CurrentChangeSetId => Value?.Id;

    public IDisposable Use(Guid? changeSetId)
    {
        var previous = Value;
        Value = changeSetId is { } id ? new Holder(id) : null;
        return new Restore(this, previous);
    }

    /// <summary>A class around the id, since <see cref="HybridAccessorBase{T}"/> holds a reference type.</summary>
    public sealed record Holder(Guid Id);

    private sealed class Restore(ChangeSetAccessor accessor, Holder? previous) : IDisposable
    {
        public void Dispose() => accessor.Value = previous;
    }
}
