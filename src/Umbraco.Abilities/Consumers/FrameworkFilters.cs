using System.Text.Json;

namespace Umbraco.Abilities.Consumers;

/// <summary>
/// Drops an ability that can't be undone where nobody can agree to it: the consumer can't ask a person before a call,
/// and no person agreed ahead of time. In the base policy.
/// </summary>
public sealed class ConsentFilter : IAbilityFilter
{
    public string Alias => FrameworkFilters.Consent;

    public string Description => "Leaves out what can't be undone where nobody can agree to it first.";

    public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
    {
        var canAgree = context.Consumer.Can(ConsumerCapabilities.AsksConsent) || context.Consumer.Can(ConsumerCapabilities.StandingConsent);
        yield return tool.Descriptor.RequiresApproval && !canAgree
            ? tool.Drop($"It can't be undone, and {context.Consumer.Name} can't ask a person to agree to it, nor did one agree ahead of time.")
            : tool;
    }
}

/// <summary>
/// Drops an ability that creates, applies or discards change sets where no conversation keeps a set open between calls.
/// There, the consumer makes and applies a set per call itself. In the base policy.
/// </summary>
public sealed class ChangeSetLifecycleFilter : IAbilityFilter
{
    public string Alias => FrameworkFilters.ChangeSetLifecycle;

    public string Description => "Leaves out the change set tools where no conversation keeps a set open.";

    public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
    {
        yield return tool.Descriptor.ManagesChangeSets && !context.Consumer.Can(ConsumerCapabilities.Conversation)
            ? tool.Drop($"It manages change sets, and {context.Consumer.Name} keeps no conversation to hold one open in.")
            : tool;
    }
}

/// <summary>
/// Drops an ability that needs something the consumer can't do (<see cref="AbilityDescriptor.Needs"/>), such as sending
/// a file. Offered there, every call would fail. In the base policy.
/// </summary>
public sealed class NeedsFilter : IAbilityFilter
{
    public string Alias => FrameworkFilters.Needs;

    public string Description => "Leaves out abilities that need something the consumer can't do.";

    public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
    {
        var missing = tool.Descriptor.Needs.Where(need => !context.Consumer.Can(need)).ToList();
        yield return missing.Count > 0
            ? tool.Drop($"It needs {string.Join(", ", missing)}, and {context.Consumer.Name} can't do that.")
            : tool;
    }
}

/// <summary>
/// Drops building blocks (<see cref="AbilityDescriptor.Granular"/>), such as setting one value by its path. A model
/// chains them; a person builds from a form, so gets the whole operation instead.
/// </summary>
public sealed class HideBuildingBlocksFilter : IAbilityFilter
{
    public string Alias => FrameworkFilters.HideBuildingBlocks;

    public string Description => "Leaves out building blocks a model chains, such as setting one value by its path.";

    public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
    {
        yield return tool.Descriptor.Granular
            ? tool.Drop("It is a building block a model chains. A person builds from a form, with whole operations.")
            : tool;
    }
}

/// <summary>Drops Explain abilities, which answer a reader in a conversation. Nobody reads their answer in a form's step.</summary>
public sealed class HideExplainFilter : IAbilityFilter
{
    public string Alias => FrameworkFilters.HideExplain;

    public string Description => "Leaves out abilities that explain, for a reader in a conversation.";

    public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
    {
        yield return tool.Descriptor.Kind == AbilityKind.Explain
            ? tool.Drop("It explains something to a reader in a conversation. Nobody reads its answer here.")
            : tool;
    }
}

/// <summary>
/// Removes the keywords meant for forms (<see cref="UiHints.FormKeywords"/>, and any a package adds) from schemas, and
/// from results, which can hold a schema. A model doesn't read them: they cost tokens and could mislead it.
/// </summary>
public sealed class StripFormKeywordsFilter(IEnumerable<string>? more = null) : IAbilityFilter, IAbilityResultFilter
{
    private readonly IReadOnlyCollection<string> _keywords = [.. UiHints.FormKeywords, .. more ?? []];

    public string Alias => FrameworkFilters.StripFormKeywords;

    public string Description => $"Removes keywords meant for forms ({string.Join(", ", _keywords)}) from schemas and results.";

    public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
    {
        var input = UiHints.Without(tool.InputSchema, _keywords);
        var output = tool.OutputSchema is { } schema ? UiHints.Without(schema, _keywords) : (JsonElement?)null;
        yield return tool with { InputSchema = input, OutputSchema = output };
    }

    public JsonElement ShapeResult(AbilityTool tool, JsonElement result, AbilityFilterContext context)
        => UiHints.Without(result, _keywords);
}

/// <summary>
/// Gives the consumer's reader their hints (<see cref="HintAttribute"/>): the ability's hint for the consumer's
/// <see cref="IAbilityConsumer.Audience"/> is added to its description, and each <c>x-hints</c> keyword in its schemas
/// and results is replaced by that audience's hint, added to the description next to it. Hints for other readers go.
/// In both framework policies, so every consumer with the same audience reads the same text.
/// </summary>
public sealed class ApplyHintsFilter : IAbilityFilter, IAbilityResultFilter
{
    public string Alias => FrameworkFilters.ApplyHints;

    public string Description => "Adds the hints for the consumer's reader to descriptions, and drops the others.";

    public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
    {
        var audience = context.Consumer.Audience;
        yield return tool with
        {
            Description = UiHints.Describe(tool.Description, tool.Descriptor.Hints, audience),
            InputSchema = UiHints.ApplyHints(tool.InputSchema, audience),
            OutputSchema = tool.OutputSchema is { } output ? UiHints.ApplyHints(output, audience) : null,
        };
    }

    public JsonElement ShapeResult(AbilityTool tool, JsonElement result, AbilityFilterContext context)
        => UiHints.ApplyHints(result, context.Consumer.Audience);
}

/// <summary>
/// Adds the <c>changeSetId</c> argument to tools whose caller chooses the change set (<see cref="ChangeSetMode.Caller"/>).
/// A conversation can hold several sets open, so a call may have to name one.
/// </summary>
public sealed class AddChangeSetArgumentFilter : IAbilityFilter
{
    public string Alias => FrameworkFilters.AddChangeSetArgument;

    public string Description => "Adds changeSetId to tools whose caller chooses the change set.";

    public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
    {
        yield return tool.ChangeSetMode == ChangeSetMode.Caller
            ? tool.WithChangeSetIdArgument().Note("Takes changeSetId: a conversation can hold several change sets open.")
            : tool;
    }
}

/// <summary>
/// Makes each Write call its own change set (<see cref="ChangeSetMode.PerCall"/>): made, applied at once, or discarded
/// if the call or the apply fails. For a consumer where each call stands alone, such as a workflow step. A read then
/// works on live data.
/// </summary>
public sealed class ChangeSetPerCallFilter : IAbilityFilter
{
    public string Alias => FrameworkFilters.ChangeSetPerCall;

    public string Description => "Makes each Write call its own change set, applied at once.";

    public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
    {
        if (tool.ChangeSetMode != ChangeSetMode.Caller)
        {
            yield return tool;
        }
        else if (tool.Descriptor.Kind == AbilityKind.Write)
        {
            yield return tool.Note("Each call is a change set of its own, applied at once.") with { ChangeSetMode = ChangeSetMode.PerCall };
        }
        else
        {
            yield return tool.Note("Reads live data: no change set stays open here.") with { ChangeSetMode = ChangeSetMode.None };
        }
    }
}

/// <summary>
/// Splits an ability by the argument it names (<see cref="AbilityInputFacts.SplitBy"/>): one tool per value, with the
/// value fixed, such as "Create Document" and "Create Discount" in place of one "Create entity". For a person picking from
/// a list. A model keeps one tool and passes the value. The values are the ability's (<see cref="IDynamicInputAbility"/>),
/// asked for each time the list is made.
/// </summary>
public sealed class SplitByChoiceFilter : IAbilityFilter
{
    public string Alias => FrameworkFilters.SplitByChoice;

    public string Description => "Makes one tool per value of the argument an ability splits by, such as one Create per entity type.";

    public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
    {
        if (tool.Descriptor.Input.SplitBy is not { } argument || tool.Ability is not IDynamicInputAbility dynamic || tool.FixedArguments.ContainsKey(argument))
        {
            yield return tool;
            yield break;
        }

        // Filters are synchronous: a list is made at once. The values are cheap to list (registered types).
        var choices = dynamic.GetChoicesAsync(argument, [], CancellationToken.None).GetAwaiter().GetResult();
        if (choices.Count == 0)
        {
            yield return tool.Drop($"It splits by {argument}, and there is no {argument} to choose.");
            yield break;
        }

        foreach (var choice in choices)
        {
            yield return tool.WithFixedArgument(argument, JsonSerializer.SerializeToElement(choice.Value)).Note($"One of the tools split by {argument}: {choice.Value}.") with
            {
                Id = $"{tool.Id}:{choice.Value}",
                Title = tool.Descriptor.Input.SplitTitle is { } title ? string.Format(System.Globalization.CultureInfo.InvariantCulture, title, choice.Name) : $"{tool.Title}: {choice.Name}",
            };
        }
    }
}

/// <summary>The framework's filters and policies.</summary>
public static class FrameworkFilters
{
    /// <summary>For a consumer whose reader is a model, in a conversation: its hints, no form keywords, and a change set argument.</summary>
    public const string ForModels = "for-models";

    /// <summary>For a consumer whose reader fills in a form, one call at a time: its hints, whole operations, each its own change set, split into one tool per choice.</summary>
    public const string ForForms = "for-forms";

    /// <summary>The base policy's filter for what can't be undone: <see cref="ConsentFilter"/>.</summary>
    public const string Consent = "consent";

    /// <summary>The base policy's filter for the change set tools: <see cref="ChangeSetLifecycleFilter"/>.</summary>
    public const string ChangeSetLifecycle = "change-set-lifecycle";

    /// <summary>The base policy's filter for what an ability needs: <see cref="NeedsFilter"/>.</summary>
    public const string Needs = "needs";

    /// <summary><see cref="ApplyHintsFilter"/>.</summary>
    public const string ApplyHints = "apply-hints";

    /// <summary><see cref="StripFormKeywordsFilter"/>.</summary>
    public const string StripFormKeywords = "strip-form-keywords";

    /// <summary><see cref="AddChangeSetArgumentFilter"/>.</summary>
    public const string AddChangeSetArgument = "add-change-set-argument";

    /// <summary><see cref="HideBuildingBlocksFilter"/>.</summary>
    public const string HideBuildingBlocks = "hide-building-blocks";

    /// <summary><see cref="HideExplainFilter"/>.</summary>
    public const string HideExplain = "hide-explain";

    /// <summary><see cref="ChangeSetPerCallFilter"/>.</summary>
    public const string ChangeSetPerCall = "change-set-per-call";

    /// <summary><see cref="SplitByChoiceFilter"/>.</summary>
    public const string SplitByChoice = "split-by-choice";

    /// <summary>
    /// Adds the framework's filters; puts the ones that protect against harm in the base policy, and groups the rest
    /// into the <see cref="ForModels"/> and <see cref="ForForms"/> policies. <paramref name="formKeywords"/> are a
    /// package's own keywords meant for forms, stripped for models with the framework's.
    /// </summary>
    public static AbilityFilterRegistry AddFrameworkFilters(this AbilityFilterRegistry filters, AbilityPolicyRegistry policies, IEnumerable<string>? formKeywords = null)
    {
        IAbilityFilter[] basePolicy = [new ConsentFilter(), new ChangeSetLifecycleFilter(), new NeedsFilter()];
        foreach (var filter in basePolicy)
        {
            filters.Add(filter);
            policies.AddToBasePolicy(filter.Alias);
        }

        IAbilityFilter strip = new StripFormKeywordsFilter(formKeywords), argument = new AddChangeSetArgumentFilter();
        IAbilityFilter blocks = new HideBuildingBlocksFilter(), explain = new HideExplainFilter(), perCall = new ChangeSetPerCallFilter();
        IAbilityFilter hints = new ApplyHintsFilter(), split = new SplitByChoiceFilter();
        filters.Add(hints).Add(strip).Add(argument).Add(blocks).Add(explain).Add(perCall).Add(split);
        policies
            .Add(ForModels, hints.Alias, strip.Alias, argument.Alias)
            .Add(ForForms, hints.Alias, blocks.Alias, explain.Alias, perCall.Alias, split.Alias);

        return filters;
    }
}
