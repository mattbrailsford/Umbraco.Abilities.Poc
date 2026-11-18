using System.ComponentModel;

using Umbraco.Abilities.Consumers;
using Umbraco.Abilities.Entities;

namespace Umbraco.Abilities.ChangeSets;

public sealed record CreateChangeSetInput([property: Description("A name a person will recognise, e.g. 'Black Friday campaign'.")] string Name);

/// <summary>What <c>changeSet.create</c> returns. A surface sees it and makes the new set current for later calls.</summary>
public sealed record ChangeSetCreated(Guid ChangeSetId, string Name);

[Ability("changeSet.create", "Create change set", Kind = AbilityKind.Write, Reversible = true, UsesChangeSet = false,
    Description = "Starts a change set: later edits are recorded in it, not saved, until a person applies it.")]
[Hint(AbilityAudiences.Model, "While it is the only change set open in this conversation, calls use it without naming it. With two or more open, every call must name one with changeSetId.")]
public sealed class CreateChangeSetAbility(ChangeSetService changeSets) : AbilityBase<CreateChangeSetInput, object>, IChangeSetLifecycleAbility
{
    protected override Task<AbilityResult> ExecuteAsync(CreateChangeSetInput input, AbilityContext context, CancellationToken cancellationToken)
    {
        var set = changeSets.Create(input.Name);
        return Task.FromResult(Success(new ChangeSetCreated(set.Id, set.Name)));
    }
}

[Ability("changeSet.validate", "Validate change set", Kind = AbilityKind.Read,
    Description = "Checks every entity in the set.")]
[Hint(AbilityAudiences.Model, "Returns problems by path; fix them with the entity edit abilities.")]
public sealed class ValidateChangeSetAbility(ChangeSetService changeSets) : AbilityBase<object>, IChangeSetLifecycleAbility
{
    protected override async Task<AbilityResult> ExecuteAsync(AbilityContext context, CancellationToken cancellationToken)
    {
        var problems = await changeSets.ValidateAsync(changeSets.GetCurrent().Id, cancellationToken);
        return Success(new { valid = problems.Count == 0, entities = problems });
    }
}

[Ability("changeSet.summarize", "Summarize change set", Kind = AbilityKind.Read,
    Description = "Lists what applying the set would create, update and run.")]
[Hint(AbilityAudiences.Model, "Show it to the person before they apply.")]
public sealed class SummarizeChangeSetAbility(ChangeSetService changeSets) : AbilityBase<ChangeSetSummary>, IChangeSetLifecycleAbility
{
    protected override async Task<AbilityResult> ExecuteAsync(AbilityContext context, CancellationToken cancellationToken)
        => Success(await changeSets.SummarizeAsync(changeSets.GetCurrent().Id, cancellationToken));
}

[Ability("changeSet.apply", "Apply change set", Kind = AbilityKind.Write,
    Description = "Saves every entity in the set and runs its recorded operations, all or nothing. This is the only step that changes live data.")]
public sealed class ApplyChangeSetAbility(ChangeSetService changeSets) : AbilityBase<IReadOnlyList<EntitySummary>>, IChangeSetLifecycleAbility
{
    protected override Task<AbilityResult> ExecuteAsync(AbilityContext context, CancellationToken cancellationToken)
        => changeSets.ApplyAsync(changeSets.GetCurrent().Id, context, cancellationToken);
}

[Ability("changeSet.discard", "Discard change set", Kind = AbilityKind.Write, Reversible = true,
    Description = "Throws the set away. No live data changes.")]
public sealed class DiscardChangeSetAbility(ChangeSetService changeSets) : AbilityBase<object>, IChangeSetLifecycleAbility
{
    protected override async Task<AbilityResult> ExecuteAsync(AbilityContext context, CancellationToken cancellationToken)
    {
        var id = changeSets.GetCurrent().Id;
        await changeSets.DiscardAsync(id, cancellationToken);
        return Success(new { discarded = id });
    }
}

public sealed record ResolveConflictInput(
    [property: Description("Entity type of the conflicting entity.")] string EntityType,
    [property: Description("Entity key.")] Guid Key,
    [property: Description("The conflict's path.")]
    [property: Hint(AbilityAudiences.Model, "As listed by changeSet.validate or changeSet.summarize.")] string Path,
    [property: Description("Which value to keep: 'yours' (the change set's) or 'theirs' (saved by someone else since).")] string Keep,
    [property: Description("The conflict's variant, e.g. { \"culture\": \"da-DK\" }.")]
    [property: Hint(AbilityAudiences.Model, "As changeSet.summarize shows it.")] Dictionary<string, string>? Variant = null);

[Ability("changeSet.resolveConflict", "Resolve change set conflict", Kind = AbilityKind.Write, Reversible = true,
    Description = "When someone else changed something the change set also changed, keeps one of the two values. Changes nothing live.")]
[Hint(AbilityAudiences.Model, "Ask the person which to keep unless the instructions already say.")]
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

/// <summary>Registers the framework's own abilities.</summary>
public static class FrameworkAbilities
{
    /// <summary>
    /// Adds the generic entity abilities (schema, find, get, create, update, set a value, add, remove and move an item, delete and
    /// move an entity) and the
    /// change set abilities. Every surface starts with these; products add their own after.
    /// </summary>
    public static AbilityRegistry AddFrameworkAbilities(this AbilityRegistry abilities, EntityAdapterRegistry adapters, ItemTypeRegistry itemTypes, ChangeSetService changeSets)
        => abilities
            .Add(new GetEntitySchemaAbility(adapters, itemTypes))
            .Add(new FindEntityAbility(adapters, changeSets))
            .Add(new GetEntityAbility(adapters, changeSets))
            .Add(new CreateEntityAbility(adapters))
            .Add(new UpdateEntityAbility(adapters))
            .Add(new SetEntityValueAbility())
            .Add(new AddEntityItemAbility())
            .Add(new RemoveEntityItemAbility())
            .Add(new MoveEntityItemAbility())
            .Add(new DeleteEntityAbility())
            .Add(new MoveEntityAbility())
            .Add(new CreateChangeSetAbility(changeSets))
            .Add(new ValidateChangeSetAbility(changeSets))
            .Add(new SummarizeChangeSetAbility(changeSets))
            .Add(new ApplyChangeSetAbility(changeSets))
            .Add(new DiscardChangeSetAbility(changeSets))
            .Add(new ResolveConflictAbility(changeSets));
}
