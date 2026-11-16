using System.ComponentModel;

namespace Umbraco.Abilities.ChangeSets;

public sealed record CreateChangeSetInput([property: Description("A name a person will recognise, e.g. 'Black Friday campaign'.")] string Name);

/// <summary>
/// The input of an ability that works on the current change set. It is empty: which set is current is ambient
/// (<see cref="IChangeSetAccessor"/>), set by whoever started the call.
/// </summary>
public sealed record CurrentChangeSetInput;

/// <summary>What <c>changeSet.create</c> returns. A surface sees it and makes the new set current for later calls.</summary>
public sealed record ChangeSetCreated(Guid ChangeSetId, string Name);

[Ability("changeSet.create", "Create change set", Kind = AbilityKind.Write, Reversible = true,
    Description = "Starts a change set and makes it the current one: later edits are recorded in it, not saved, until a person applies it.")]
public sealed class CreateChangeSetAbility(ChangeSetService changeSets) : AbilityBase<CreateChangeSetInput, object>, IChangeSetLifecycleAbility
{
    protected override Task<AbilityResult> ExecuteAsync(CreateChangeSetInput input, AbilityContext context, CancellationToken cancellationToken)
    {
        var set = changeSets.Create(input.Name);
        return Task.FromResult(Success(new ChangeSetCreated(set.Id, set.Name)));
    }
}

[Ability("changeSet.validate", "Validate change set", Kind = AbilityKind.Read,
    Description = "Checks every entity in the set. Returns problems by path; fix them with the entity edit abilities.")]
public sealed class ValidateChangeSetAbility(ChangeSetService changeSets) : AbilityBase<CurrentChangeSetInput, object>, IChangeSetLifecycleAbility
{
    protected override async Task<AbilityResult> ExecuteAsync(CurrentChangeSetInput input, AbilityContext context, CancellationToken cancellationToken)
    {
        var problems = await changeSets.ValidateAsync(changeSets.GetCurrent().Id, cancellationToken);
        return Success(new { valid = problems.Count == 0, entities = problems });
    }
}

[Ability("changeSet.summarize", "Summarize change set", Kind = AbilityKind.Read,
    Description = "Lists what applying the set would create, update and run. Show it to the person before they apply.")]
public sealed class SummarizeChangeSetAbility(ChangeSetService changeSets) : AbilityBase<CurrentChangeSetInput, ChangeSetSummary>, IChangeSetLifecycleAbility
{
    protected override async Task<AbilityResult> ExecuteAsync(CurrentChangeSetInput input, AbilityContext context, CancellationToken cancellationToken)
        => Success(await changeSets.SummarizeAsync(changeSets.GetCurrent().Id, cancellationToken));
}

[Ability("changeSet.apply", "Apply change set", Kind = AbilityKind.Write,
    Description = "Saves every entity in the set and runs its recorded operations, all or nothing. This is the only step that changes live data.")]
public sealed class ApplyChangeSetAbility(ChangeSetService changeSets) : AbilityBase<CurrentChangeSetInput, IReadOnlyList<EntitySummary>>, IChangeSetLifecycleAbility
{
    protected override Task<AbilityResult> ExecuteAsync(CurrentChangeSetInput input, AbilityContext context, CancellationToken cancellationToken)
        => changeSets.ApplyAsync(changeSets.GetCurrent().Id, context, cancellationToken);
}

[Ability("changeSet.discard", "Discard change set", Kind = AbilityKind.Write, Reversible = true,
    Description = "Throws the set away. No live data changes.")]
public sealed class DiscardChangeSetAbility(ChangeSetService changeSets) : AbilityBase<CurrentChangeSetInput, object>, IChangeSetLifecycleAbility
{
    protected override async Task<AbilityResult> ExecuteAsync(CurrentChangeSetInput input, AbilityContext context, CancellationToken cancellationToken)
    {
        var id = changeSets.GetCurrent().Id;
        await changeSets.DiscardAsync(id, cancellationToken);
        return Success(new { discarded = id });
    }
}

public sealed record ResolveConflictInput(
    [property: Description("Entity type of the conflicting entity.")] string EntityType,
    [property: Description("Entity key.")] Guid Key,
    [property: Description("The conflict's path, as listed by changeSet.validate or changeSet.summarize.")] string Path,
    [property: Description("Which value to keep: 'yours' (the change set's) or 'theirs' (saved by someone else since).")] string Keep,
    [property: Description("The conflict's variant, as changeSet.summarize shows it, e.g. { \"culture\": \"da-DK\" }.")] Dictionary<string, string>? Variant = null);

[Ability("changeSet.resolveConflict", "Resolve change set conflict", Kind = AbilityKind.Write, Reversible = true,
    Description = "When someone else changed something the change set also changed, keeps one of the two values. " +
                  "Ask the person which to keep unless the instructions already say. Changes nothing live.")]
public sealed class ResolveConflictAbility(ChangeSetService changeSets) : AbilityBase<ResolveConflictInput, object>, IChangeSetLifecycleAbility
{
    protected override async Task<AbilityResult> ExecuteAsync(ResolveConflictInput input, AbilityContext context, CancellationToken cancellationToken)
    {
        var keep = input.Keep?.ToLowerInvariant() switch
        {
            "yours" => ConflictSide.Yours,
            "theirs" => ConflictSide.Theirs,
            _ => throw new AbilityInputException("Keep must be 'yours' or 'theirs'.", [new Problem("keep", "Must be 'yours' or 'theirs'.")]),
        };

        await changeSets.ResolveConflictAsync(changeSets.GetCurrent().Id, input.EntityType, input.Key, input.Path, Entities.VariantId.From(input.Variant), keep, cancellationToken);
        return Success(new { resolved = input.Path, kept = keep.ToString().ToLowerInvariant() });
    }
}
