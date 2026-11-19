using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Samples.Automate;

/// <summary>An Automate workflow's editing contract: one trigger, then steps whose item types are plugins and abilities.</summary>
[Entity("umbraco-automate-workflow", "Workflow", Description = "An automation: a trigger, then steps.")]
public sealed class WorkflowEditModel : EntityModel
{
    [EntityName, Required, Description("Workflow name.")]
    public string? Name { get; set; }

    [Description("Whether the workflow runs.")]
    public bool? Enabled { get; set; }

    [Items(WorkflowAdapter.TriggerGroup, Min = 1, Max = 1), Description("What starts the workflow.")]
    public List<DynamicItem> Trigger { get; set; } = [];

    [Items(WorkflowAdapter.StepGroup, Min = 1), Description("The steps. Each step names the step to run next, once per exit.")]
    public List<DynamicItem> Steps { get; set; } = [];
}

/// <summary>One link in Automate's stored form: from a step, to the step it leads to, for an outcome or for any result.</summary>
public sealed record StepConnection(Guid SourceStepId, Guid TargetStepId, string? Outcome);
