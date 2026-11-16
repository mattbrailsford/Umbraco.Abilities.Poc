using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Umbraco.Abilities.Samples.Automate.Plugins;

// A stand-in for Automate's plugin model, shaped like what Umbraco.Commerce.Automate uses: [Trigger], [Action] and
// [ControlFlow] attributes with required sections, and typed settings classes whose properties carry [Field]. An action
// or control flow can name its outcomes (as on Automate's action-outcomes branch): each is an exit an author connects
// to its own next step. Packages register their plugins in collections. Nothing here knows about abilities.

[AttributeUsage(AttributeTargets.Class)]
public sealed class TriggerAttribute(string alias, string name) : Attribute
{
    public string Alias { get; } = alias;

    public string Name { get; } = name;

    public string? Description { get; set; }

    public string[] RequiredSections { get; set; } = [];
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class ActionAttribute(string alias, string name) : Attribute
{
    public string Alias { get; } = alias;

    public string Name { get; } = name;

    public string? Description { get; set; }

    public string[] RequiredSections { get; set; } = [];
}

/// <summary>A control flow step, such as If: it routes the run rather than doing work.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ControlFlowAttribute(string alias, string name) : Attribute
{
    public string Alias { get; } = alias;

    public string Name { get; } = name;

    public string? Description { get; set; }

    public string[] RequiredSections { get; set; } = [];
}

/// <summary>
/// A named result a step can finish with. Authors route each outcome to its own next step. The key is stored on the
/// automation's connections. As in Automate, at most one outcome is the default.
/// </summary>
public sealed record StepOutcome(string Key, string Label)
{
    public bool IsDefault { get; init; }

    /// <summary>When the step finishes with this outcome.</summary>
    public string? Description { get; init; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class FieldAttribute : Attribute
{
    public string? Label { get; set; }

    public string? Description { get; set; }

    public string? EditorUiAlias { get; set; }

    public int SortOrder { get; set; }

    /// <summary>The value can be a binding to an earlier step's output instead of a literal.</summary>
    public bool SupportsBindings { get; set; }
}

public abstract class TriggerBase<TSettings>;

/// <summary>A step type: an action or a control flow.</summary>
public interface IStepType
{
    /// <summary>The outcomes the step can finish with. None: the step has one unnamed exit.</summary>
    IReadOnlyList<StepOutcome> GetOutcomes();
}

/// <summary>A step's settings and outcomes.</summary>
public abstract class StepTypeBase<TSettings> : IStepType
{
    public virtual IReadOnlyList<StepOutcome> GetOutcomes() => [];
}

public abstract class ActionBase<TSettings> : StepTypeBase<TSettings>;

public abstract class ControlFlowBase<TSettings> : StepTypeBase<TSettings>;

/// <summary>A registered trigger, action or control flow, as Automate describes it.</summary>
public sealed record AutomatePlugin(
    string Alias,
    string Name,
    string? Description,
    IReadOnlyList<string> RequiredSections,
    IReadOnlyList<AutomateField> Fields,
    IReadOnlyList<StepOutcome> Outcomes);

public sealed record AutomateField(string Key, FieldAttribute Field, Type PropertyType, IReadOnlyList<ValidationAttribute> Rules);

/// <summary>Registered triggers, actions or control flows. Packages add theirs.</summary>
public sealed class AutomatePluginCollection : List<Type>
{
    public IEnumerable<AutomatePlugin> Plugins => this.Select(Describe);

    private static AutomatePlugin Describe(Type type)
    {
        var (alias, name, description, sections) = type.GetCustomAttribute<TriggerAttribute>() is { } trigger
            ? (trigger.Alias, trigger.Name, trigger.Description, trigger.RequiredSections)
            : type.GetCustomAttribute<ActionAttribute>() is { } action
                ? (action.Alias, action.Name, action.Description, action.RequiredSections)
                : type.GetCustomAttribute<ControlFlowAttribute>() is { } controlFlow
                    ? (controlFlow.Alias, controlFlow.Name, controlFlow.Description, controlFlow.RequiredSections)
                    : throw new InvalidOperationException($"{type.Name} is not a trigger, action or control flow.");

        var settings = BaseOf(type).GetGenericArguments()[0];
        var fields = settings.GetProperties()
            .Select(p => (Property: p, Field: p.GetCustomAttribute<FieldAttribute>()))
            .Where(p => p.Field is not null)
            .OrderBy(p => p.Field!.SortOrder)
            .Select(p => new AutomateField(char.ToLowerInvariant(p.Property.Name[0]) + p.Property.Name[1..], p.Field!, p.Property.PropertyType, p.Property.GetCustomAttributes<ValidationAttribute>().ToList()))
            .ToList();

        var outcomes = Activator.CreateInstance(type) is IStepType step ? step.GetOutcomes() : [];
        return new AutomatePlugin(alias, name, description, sections, fields, outcomes);
    }

    // The generic base that names the settings type: TriggerBase<T>, ActionBase<T> or ControlFlowBase<T>.
    private static Type BaseOf(Type type)
    {
        var current = type.BaseType!;
        while (!current.IsGenericType)
        {
            current = current.BaseType!;
        }

        return current;
    }
}

// ── Automate's own triggers ─────────────────────────────────────────────────────────────────────────────────

public sealed class FormSubmittedSettings
{
    [Field(Label = "Form", Description = "The form's name.")]
    [Required]
    public string? Form { get; set; }
}

[Trigger("formSubmitted", "Form submitted", Description = "Runs when a form is submitted.")]
public sealed class FormSubmittedTrigger : TriggerBase<FormSubmittedSettings>;

public sealed class ScheduleSettings
{
    [Field(Label = "Cron", Description = "A cron expression.")]
    [Required]
    public string? Cron { get; set; }
}

[Trigger("schedule", "Schedule", Description = "Runs on a schedule.")]
public sealed class ScheduleTrigger : TriggerBase<ScheduleSettings>;

// ── Automate's own steps ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>One condition: compares two operands, either of which can be a binding.</summary>
public sealed class Condition
{
    public string LeftOperand { get; set; } = string.Empty;

    public ConditionOperator Operator { get; set; }

    public string RightOperand { get; set; } = string.Empty;
}

public enum ConditionOperator
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    GreaterThan,
    LessThan,
    GreaterThanOrEquals,
    LessThanOrEquals,
    IsEmpty,
    IsNotEmpty,
}

/// <summary>Conditions joined by AND: all must be true.</summary>
public sealed class ConditionGroup
{
    public List<Condition> Conditions { get; set; } = [];
}

/// <summary>Groups joined by OR: at least one must pass. (A AND B) OR (C AND D).</summary>
public sealed class ConditionSet
{
    public List<ConditionGroup> Groups { get; set; } = [];
}

public sealed class IfControlFlowSettings
{
    [Field(Label = "Conditions", Description = "Define conditions to determine which branch to follow.",
        EditorUiAlias = "UmbracoAutomate.PropertyEditorUi.ConditionBuilder", SupportsBindings = true)]
    [Required]
    public ConditionSet Conditions { get; set; } = new();
}

/// <summary>
/// Automate's If. Automate returns "true" or "false" from its step body and special-cases them in the editor. Here it
/// names them as outcomes, the way any action can on the action-outcomes branch, so it needs no special case.
/// </summary>
[ControlFlow("umbracoAutomate.if", "If", Description = "Evaluates conditions and routes to the true or false branch.")]
public sealed class IfControlFlow : ControlFlowBase<IfControlFlowSettings>
{
    public override IReadOnlyList<StepOutcome> GetOutcomes() =>
    [
        new StepOutcome("true", "True") { Description = "The conditions match." },
        new StepOutcome("false", "False") { Description = "The conditions don't match." },
    ];
}

public sealed class GetContentSettings
{
    [Field(Label = "Content Key", Description = "The key of the content item to fetch.", SupportsBindings = true)]
    [Required]
    public string ContentKey { get; set; } = string.Empty;
}

/// <summary>
/// Automate's Get Content, with its outcomes as the action-outcomes branch declares them: labels are localization keys,
/// and there are no descriptions.
/// </summary>
[Action("umbracoAutomate.getContent", "Get Content", Description = "Fetches a published content item and exposes its properties for use in later steps.")]
public sealed class GetContentAction : ActionBase<GetContentSettings>
{
    public override IReadOnlyList<StepOutcome> GetOutcomes() =>
    [
        new StepOutcome("success", "#uaOutcomes_found") { IsDefault = true },
        new StepOutcome("notFound", "#uaOutcomes_notFound"),
    ];
}
