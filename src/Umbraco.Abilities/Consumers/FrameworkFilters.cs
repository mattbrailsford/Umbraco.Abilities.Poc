using System.Text.Json;

namespace Umbraco.Abilities.Consumers;

/// <summary>
/// Drops an ability that can't be undone where nobody can agree to it: the consumer can't ask a person before a call,
/// and no person agreed ahead of time. In the base policy.
/// </summary>
public sealed class ConsentFilter : IAbilityFilter
{
    public string Alias => "consent";

    public string Description => "Leaves out what can't be undone where nobody can agree to it first.";

    public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
    {
        var canAgree = context.Consumer.Capabilities.HasFlag(ConsumerCapabilities.AsksConsent)
            || context.Consumer.Capabilities.HasFlag(ConsumerCapabilities.StandingConsent);
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
    public string Alias => "change-set-lifecycle";

    public string Description => "Leaves out the change set tools where no conversation keeps a set open.";

    public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
    {
        yield return tool.Descriptor.ManagesChangeSets && !context.Consumer.Capabilities.HasFlag(ConsumerCapabilities.Conversation)
            ? tool.Drop($"It manages change sets, and {context.Consumer.Name} keeps no conversation to hold one open in.")
            : tool;
    }
}

/// <summary>
/// Drops building blocks (<see cref="AbilityDescriptor.Granular"/>), such as setting one value by its path. A model
/// chains them; a person builds from a form, so gets the whole operation instead.
/// </summary>
public sealed class HideBuildingBlocksFilter : IAbilityFilter
{
    public string Alias => "hide-building-blocks";

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
    public string Alias => "hide-explain";

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

    public string Alias => "strip-form-keywords";

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
/// Adds the <c>changeSetId</c> argument to tools whose caller chooses the change set (<see cref="ChangeSetMode.Caller"/>).
/// A conversation can hold several sets open, so a call may have to name one.
/// </summary>
public sealed class AddChangeSetArgumentFilter : IAbilityFilter
{
    public string Alias => "add-change-set-argument";

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
    public string Alias => "change-set-per-call";

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

/// <summary>The framework's filters and policies.</summary>
public static class FrameworkFilters
{
    /// <summary>For a consumer whose reader is a model, in a conversation: no form keywords, and a change set argument.</summary>
    public const string ForModels = "for-models";

    /// <summary>For a consumer whose reader fills in a form, one call at a time: whole operations, each its own change set.</summary>
    public const string ForForms = "for-forms";

    /// <summary>
    /// Adds the framework's filters; puts the ones that protect against harm in the base policy, and groups the rest
    /// into the <see cref="ForModels"/> and <see cref="ForForms"/> policies. <paramref name="formKeywords"/> are a
    /// package's own keywords meant for forms, stripped for models with the framework's.
    /// </summary>
    public static AbilityFilterRegistry AddFrameworkFilters(this AbilityFilterRegistry filters, AbilityPolicyRegistry policies, IEnumerable<string>? formKeywords = null)
    {
        IAbilityFilter[] basePolicy = [new ConsentFilter(), new ChangeSetLifecycleFilter()];
        foreach (var filter in basePolicy)
        {
            filters.Add(filter);
            policies.AddToBasePolicy(filter.Alias);
        }

        IAbilityFilter strip = new StripFormKeywordsFilter(formKeywords), argument = new AddChangeSetArgumentFilter();
        IAbilityFilter blocks = new HideBuildingBlocksFilter(), explain = new HideExplainFilter(), perCall = new ChangeSetPerCallFilter();
        filters.Add(strip).Add(argument).Add(blocks).Add(explain).Add(perCall);
        policies
            .Add(ForModels, strip.Alias, argument.Alias)
            .Add(ForForms, blocks.Alias, explain.Alias, perCall.Alias);

        return filters;
    }
}
