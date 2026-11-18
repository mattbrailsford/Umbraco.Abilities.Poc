using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.ChangeSets;

namespace Umbraco.Abilities;

/// <summary>
/// Runs a tool's calls, for every consumer: an AI tool, a Management API endpoint, an MCP tool, an Automate action. Each
/// consumer translates its tools; none repeats this.
/// <para>
/// A tool in <see cref="ChangeSetMode.Caller"/> mode works in the set its call names with <c>changeSetId</c>, or else
/// the only set open in the conversation, which is ambient for that call only (<see cref="IChangeSetAccessor"/>). With
/// two or more open, a call must name one, so an edit never lands in a set the caller didn't mean. Every result says
/// which set the call used.
/// </para>
/// <para>
/// A call to an ability that can't be undone (<see cref="AbilityDescriptor.RequiresApproval"/>) runs only when the
/// consumer says the person agreed to it. That is decided per call: in a change set the call is only recorded, which can
/// be undone, so it needs no consent; applying the set does. This is consent, not review: the person the caller acts
/// for says yes, so a model can't post by accident. How the consumer asks is its own: an AI chat pauses for the person,
/// an MCP server asks through its client, an HTTP caller sends a header once the person agreed. Without it, nothing is
/// done.
/// </para>
/// </summary>
public sealed class AbilityToolRunner(AbilityInvoker invoker)
{
    /// <summary>The abilities and change sets calls run against.</summary>
    public AbilityInvoker Invoker => invoker;

    /// <summary>
    /// Runs one call of <paramref name="tool"/> with its input as a JSON object, in <paramref name="session"/>'s
    /// conversation. The tool's fixed arguments are added, and win over the caller's. Failures come back as data, never
    /// as exceptions, so a caller can correct an input failure and stop on the others. The result is the same shape for
    /// every consumer: see <see cref="AbilityToolResult"/>.
    /// </summary>
    /// <param name="tool">The tool to run, as its consumer was offered it.</param>
    /// <param name="arguments">The tool's input.</param>
    /// <param name="session">The conversation: the user the caller acts for, and the change sets open in it.</param>
    /// <param name="consent">
    /// The person's consent to this call. Only a call that can't be undone needs it: an ability that requires approval,
    /// run live rather than recorded in a change set. Without it, such a call does nothing and fails with
    /// <see cref="AbilityOperationStatus.ApprovalRequired"/> and an <see cref="AbilityToolResult.Approval"/> request:
    /// the consumer asks the person that question and, on yes, calls again with <see cref="AbilityConsent.For"/> its id.
    /// </param>
    /// <param name="cancellationToken">Stops the call.</param>
    /// <param name="changeSetName">
    /// For a tool in <see cref="ChangeSetMode.PerCall"/> mode: the name of the set the call makes, so a person can tell
    /// where it came from. The tool's title when not given.
    /// </param>
    public async Task<AbilityToolResult> InvokeAsync(
        AbilityTool tool,
        JsonObject arguments,
        AbilityToolSession session,
        AbilityConsent consent = default,
        CancellationToken cancellationToken = default,
        string? changeSetName = null)
    {
        if (tool.ChangeSetMode == ChangeSetMode.PerCall)
        {
            return await InvokePerCallAsync(tool, arguments, session, consent, changeSetName ?? tool.Title, cancellationToken);
        }

        var changeSets = invoker.ChangeSets;
        var callerChooses = tool.ChangeSetMode == ChangeSetMode.Caller;
        var input = new JsonObject();
        Guid? named = null;
        foreach (var (name, value) in arguments)
        {
            if (callerChooses && name == AbilityTool.ChangeSetIdArgument)
            {
                if (value is null || value.GetValueKind() == JsonValueKind.Null)
                {
                    continue;
                }

                if (value.GetValueKind() != JsonValueKind.String || !Guid.TryParse(value.GetValue<string>(), out var id))
                {
                    return AbilityToolResult.From(AbilityResult.Fail(AbilityOperationStatus.InvalidInput, "changeSetId must be a change set's id.",
                        [new Problem(AbilityTool.ChangeSetIdArgument, "Not an id.")]), null);
                }

                named = id;
                continue;
            }

            input[name] = value?.DeepClone();
        }

        foreach (var (name, value) in tool.FixedArguments)
        {
            input[name] = JsonNode.Parse(value.GetRawText());
        }

        // Sets applied or discarded since the last call (here or elsewhere) are no longer open.
        foreach (var closed in session.OpenChangeSetIds.Where(id => changeSets.Find(id) is null).ToList())
        {
            session.Close(closed);
        }

        ChangeSet? used;
        if (named is { } given)
        {
            // A set that is named must exist: never fall back to live data because of a wrong or stale id.
            used = changeSets.Find(given);
            if (used is null)
            {
                return AbilityToolResult.From(AbilityResult.Fail(AbilityOperationStatus.InvalidInput,
                    $"No change set {given}. It may have been applied or discarded.",
                    [new Problem(AbilityTool.ChangeSetIdArgument, "Not found.")]), null);
            }

            session.Open(given);
        }
        else if (callerChooses && session.OpenChangeSetIds.Count > 1)
        {
            // Guessing between two open sets could put an edit in the wrong one, so ask.
            var open = string.Join(", ", session.OpenChangeSetIds.Select(id => $"'{changeSets.Get(id).Name}' ({id})"));
            return AbilityToolResult.From(AbilityResult.Fail(AbilityOperationStatus.InvalidInput,
                $"{session.OpenChangeSetIds.Count} change sets are open in this conversation: {open}. Name the one to use with changeSetId.",
                [new Problem(AbilityTool.ChangeSetIdArgument, "Required while more than one change set is open.")]), null);
        }
        else
        {
            used = callerChooses && session.ChangeSetId is { } only ? changeSets.Find(only) : null;
        }

        var context = new AbilityContext(session.UserKey);
        if (await CheckConsentAsync(tool.Descriptor, input, used, consent, context, cancellationToken) is { } refused)
        {
            return refused;
        }

        AbilityResult result;
        using (changeSets.Use(used?.Id))
        {
            result = await invoker.InvokeAsync(tool.Descriptor.Id, input, context, cancellationToken);
        }

        var usedSet = used is null ? null : new AbilityToolChangeSet(used.Id, used.Name);
        if (result.Result is ChangeSetCreated created)
        {
            session.Open(created.ChangeSetId);
            usedSet = new AbilityToolChangeSet(created.ChangeSetId, created.Name);
        }
        else if (used is not null && changeSets.Find(used.Id) is null)
        {
            // Applied or discarded: it leaves the conversation.
            session.Close(used.Id);
            usedSet = usedSet! with { Open = false };
        }

        return AbilityToolResult.From(result, usedSet);
    }

    // A set of the call's own: record the call, apply the set, and leave nothing behind if either fails. Applying is what
    // can't be undone, so that is what needs consent.
    private async Task<AbilityToolResult> InvokePerCallAsync(
        AbilityTool tool, JsonObject arguments, AbilityToolSession session, AbilityConsent consent, string name, CancellationToken cancellationToken)
    {
        var descriptor = tool.Descriptor;
        if (descriptor.RequiresApproval && !consent.IsGiven)
        {
            return AbilityToolResult.From(AbilityResult.Fail(AbilityOperationStatus.ApprovalRequired,
                $"{descriptor.Name} can't be undone, so the person you act for must agree to it first. Nothing was done.", []), null);
        }

        var changeSets = invoker.ChangeSets;
        var set = changeSets.Create(name);
        var input = new JsonObject();
        foreach (var (key, value) in arguments)
        {
            input[key] = value?.DeepClone();
        }

        foreach (var (key, value) in tool.FixedArguments)
        {
            input[key] = JsonNode.Parse(value.GetRawText());
        }

        var context = new AbilityContext(session.UserKey);
        AbilityResult result;
        using (changeSets.Use(set.Id))
        {
            result = await invoker.InvokeAsync(descriptor.Id, input, context, cancellationToken);
        }

        if (result.Success)
        {
            var applied = await changeSets.ApplyAsync(set.Id, context, cancellationToken);
            if (!applied.Success)
            {
                result = applied;
            }
        }

        if (!result.Success && changeSets.Find(set.Id) is not null)
        {
            await changeSets.DiscardAsync(set.Id, cancellationToken);
        }

        return AbilityToolResult.From(result, new AbilityToolChangeSet(set.Id, set.Name, Open: false));
    }

    // Null when the call may run. In a set, a Write is recorded, not run (see AbilityInvoker), so only a call that runs
    // live needs consent. Applying with no set open needs none: it fails on its own.
    private async Task<AbilityToolResult?> CheckConsentAsync(
        AbilityDescriptor descriptor, JsonObject input, ChangeSet? used, AbilityConsent consent, AbilityContext context, CancellationToken cancellationToken)
    {
        var lifecycle = descriptor.ManagesChangeSets;
        var recorded = used is not null && descriptor.Kind == AbilityKind.Write && !lifecycle;
        if (!descriptor.RequiresApproval || recorded || (lifecycle && used is null) || consent.IsGiven)
        {
            return null;
        }

        // The approval binds to the call as the ability gets it: the input without the change set id, which is bound apart.
        var call = input.ToJsonString();
        var set = lifecycle ? used : null;
        string? why = null;
        if (consent.ApprovalId is { } id)
        {
            why = invoker.Approvals.Consume(id, context, descriptor.Id, call, set);
            if (why is null)
            {
                return null;
            }
        }

        var summary = set is null ? null : await invoker.ChangeSets.SummarizeAsync(set.Id, cancellationToken);
        var request = invoker.Approvals.Request(context, descriptor.Id, call, set, Question(descriptor, input, summary), summary);
        var message = $"{(why is null ? string.Empty : why + " ")}{descriptor.Name} can't be undone, so the person you act for must agree to it first. Nothing was done.";
        return AbilityToolResult.From(AbilityResult.Fail(AbilityOperationStatus.ApprovalRequired, message, []),
            used is null ? null : new AbilityToolChangeSet(used.Id, used.Name)) with { Approval = request };
    }

    // What to ask the person: the call in plain words, and for a change set what applying it does.
    private static string Question(AbilityDescriptor descriptor, JsonObject input, ChangeSetSummary? summary)
    {
        if (summary is null)
        {
            return input.Count == 0
                ? $"Allow \"{descriptor.Name}\"? It can't be undone."
                : $"Allow \"{descriptor.Name}\"? It can't be undone.\nWith: {input.ToJsonString()}";
        }

        var lines = new List<string> { $"{descriptor.Name} \"{summary.Name}\"? It can't be undone." };
        lines.AddRange(summary.Entities.Select(e => $"- {e.TypeName ?? e.Entity.EntityType} \"{e.Entity.Name}\" ({e.Change})"));
        if (summary.NeedsConsent is { Count: > 0 } needs)
        {
            lines.Add("It also does these, which can't be undone either:");
            lines.AddRange(needs.Select(n => $"- {n}"));
        }

        if (summary.Rebased.Count > 0)
        {
            lines.Add($"{summary.Rebased.Count} of these were changed by someone else since; their changes are kept.");
        }

        return string.Join("\n", lines);
    }
}
