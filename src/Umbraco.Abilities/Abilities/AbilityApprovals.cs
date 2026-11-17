using System.Collections.Concurrent;

using Umbraco.Abilities.ChangeSets;

namespace Umbraco.Abilities;

/// <summary>
/// What a surface says about the person's consent to one call. Only a call that can't be undone needs it.
/// </summary>
public readonly record struct AbilityConsent
{
    /// <summary>The surface didn't ask. A call that needs consent gets an approval request back instead of running.</summary>
    public static AbilityConsent None => default;

    /// <summary>
    /// The surface asked the person itself, in the same exchange, and they said yes. For a host that pauses the call
    /// for a person, such as an AI chat with M.E.AI's approval.
    /// </summary>
    public static AbilityConsent Given => new() { IsGiven = true };

    /// <summary>
    /// The person said yes to the approval request with this id. It approves only the call it was made for, once, and
    /// for a change set only the version they saw.
    /// </summary>
    public static AbilityConsent For(Guid approvalId) => new() { ApprovalId = approvalId };

    public bool IsGiven { get; private init; }

    public Guid? ApprovalId { get; private init; }
}

/// <summary>
/// A call that needs the person's consent, waiting for it. The surface shows <see cref="Question"/> to the person and,
/// if they agree, calls again with <see cref="AbilityConsent.For"/> this id.
/// </summary>
/// <param name="Id">Names this request. It approves one call: this ability, this input, this user, and for a change set this version.</param>
/// <param name="Question">What to ask the person, in plain words, with what the call will do.</param>
/// <param name="Summary">For applying a change set: what it will create, change and run, at the version the person is asked about.</param>
/// <param name="ExpiresAt">After this, the id approves nothing and the call must be asked about again.</param>
public sealed record AbilityApprovalRequest(Guid Id, string Question, ChangeSetSummary? Summary, DateTimeOffset ExpiresAt);

/// <summary>
/// Approval requests waiting for a person, for surfaces where asking happens between two calls, such as HTTP. Each is
/// tied to one call, so a yes approves that call only, never the next one, and several can wait at once. Kept in memory,
/// so one server only.
/// </summary>
public sealed class AbilityApprovals
{
    /// <summary>How long a person has to answer.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();

    internal AbilityApprovalRequest Request(AbilityContext context, string abilityId, string input, ChangeSet? set, string question, ChangeSetSummary? summary)
    {
        var request = new AbilityApprovalRequest(Guid.NewGuid(), question, summary, DateTimeOffset.UtcNow + Lifetime);
        _pending[request.Id] = new Pending(context.UserKey, abilityId, input, set?.Id, summary?.Version ?? set?.Version, request.ExpiresAt);
        return request;
    }

    /// <summary>
    /// Uses up an approval for one call. Null when it approves this call; otherwise why not. Used up either way, so an
    /// id never approves twice.
    /// </summary>
    internal string? Consume(Guid id, AbilityContext context, string abilityId, string input, ChangeSet? set)
    {
        if (!_pending.TryRemove(id, out var pending))
        {
            return "That approval is unknown or already used.";
        }

        return pending.ExpiresAt < DateTimeOffset.UtcNow ? "That approval has expired."
            : pending.UserKey != context.UserKey ? "That approval was for someone else."
            : pending.AbilityId != abilityId || pending.Input != input || pending.ChangeSetId != set?.Id ? "That approval was for a different call."
            : set is not null && pending.ChangeSetVersion != set.Version ? "The change set has changed since the person agreed."
            : null;
    }

    private sealed record Pending(Guid UserKey, string AbilityId, string Input, Guid? ChangeSetId, long? ChangeSetVersion, DateTimeOffset ExpiresAt);
}
