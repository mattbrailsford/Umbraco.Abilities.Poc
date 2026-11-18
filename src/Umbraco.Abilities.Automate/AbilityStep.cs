using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Bindings;
using Umbraco.Automate.Core.Settings;

namespace Umbraco.Abilities.Automate;

/// <summary>
/// How a step runs abilities, for every action the loader makes. A step runs as the workspace's service account. Its
/// Write calls are recorded into one change set of their own, then applied at once: abilities only write through change
/// sets. Publishing the automation is the person's consent; nothing waits for them per run. A failed call or apply
/// leaves nothing behind.
/// </summary>
public sealed partial class AbilityStep(AbilityInvoker invoker, BindingEvaluator bindings)
{
    /// <summary>The abilities a step calls.</summary>
    public AbilityInvoker Invoker => invoker;

    /// <summary>Runs a step's tool calls.</summary>
    public AbilityToolRunner Runner { get; } = new(invoker);

    /// <summary>
    /// Runs <paramref name="calls"/> as the service account. With <paramref name="writes"/>, the calls are recorded
    /// into a change set made for this step, and the set is applied when they all succeed. <paramref name="output"/>
    /// then gives the step's output, after the apply.
    /// </summary>
    public async Task<ActionResult> RunAsync(
        ActionContext context,
        string name,
        bool writes,
        Func<AbilityToolSession, Task<StepCall>> calls,
        Func<StepCall, Guid, Task<object?>>? output,
        CancellationToken cancellationToken)
    {
        if (context.ExecutionContext?.ServiceAccountKey is not { } serviceAccount || serviceAccount == Guid.Empty)
        {
            return ActionResult.Failed(new ValidationException("The workspace has no service account to run the ability as."), StepRunErrorCategory.ConfigurationError);
        }

        var session = new AbilityToolSession(serviceAccount);
        var set = writes
            ? invoker.ChangeSets.Create($"Automate: {context.ExecutionContext.AutomationName}, run {context.RunId}, step {context.StepId}")
            : null;
        if (set is not null)
        {
            session.Open(set.Id);
        }

        StepCall result;
        try
        {
            result = await calls(session);
        }
        catch (AbilityInputException ex)
        {
            result = StepCall.Fail(AbilityOperationStatus.InvalidInput, ex.Message, ex.Problems);
        }

        if (!result.Success)
        {
            await DiscardAsync(set);
            return Failed(name, result.Status, result.Message, result.Problems);
        }

        if (set is not null)
        {
            var applied = await invoker.ChangeSets.ApplyAsync(set.Id, new AbilityContext(serviceAccount), cancellationToken);
            if (!applied.Success)
            {
                await DiscardAsync(set);
                return Failed(name, applied.Status, applied.Message, applied.Problems);
            }
        }

        var value = output is null ? result.Output : await output(result, serviceAccount);
        var json = JsonSerializer.SerializeToNode(value, AbilityJson.Options);
        return json is null ? ActionResult.Success() : Output.Success(json);
    }

    /// <summary>
    /// Settings with their <c>${ … }</c> bindings resolved. A value that is one whole binding keeps its type (a number
    /// stays a number); text around a binding makes a string. <paramref name="bound"/> names the top-level settings
    /// that held a binding.
    /// </summary>
    public JsonObject Resolve(JsonObject settings, ActionContext context, out ISet<string> bound)
    {
        var data = context.BindingData ?? new Dictionary<string, object?>();
        bound = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new JsonObject();
        foreach (var (name, value) in settings)
        {
            if (value?.ToJsonString().Contains("${", StringComparison.Ordinal) == true)
            {
                bound.Add(name);
            }

            resolved[name] = Resolve(value?.DeepClone(), data);
        }

        return resolved;
    }

    /// <summary>A step's settings, as Automate hands them over, as JSON.</summary>
    public static JsonObject ToJson(Dictionary<string, object?> settings)
        => new(settings.Select(s => KeyValuePair.Create(s.Key, ToNode(s.Value))));

    /// <summary>Any value as JSON.</summary>
    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        _ => JsonSerializer.SerializeToNode(value, AbilityJson.Options),
    };

    private async Task DiscardAsync(ChangeSet? set)
    {
        if (set is not null && invoker.ChangeSets.Find(set.Id) is not null)
        {
            await invoker.ChangeSets.DiscardAsync(set.Id);
        }
    }

    // A call the ability refused won't succeed on a retry, so it stops the run. "Failed" is after the ability's own
    // retries (AbilityDescriptor.Retries): Automate retrying it again could repeat a Write that half ran.
    private static ActionResult Failed(string name, AbilityOperationStatus status, string? message, IReadOnlyList<Problem>? problems)
    {
        var details = problems is { Count: > 0 } list ? " " + string.Join(" ", list.Select(p => $"{p.Path}: {p}")) : string.Empty;
        var error = new ValidationException($"{name}: {message}{details}".Trim());
        return ActionResult.Failed(error, status switch
        {
            AbilityOperationStatus.InvalidInput => StepRunErrorCategory.Validation,
            AbilityOperationStatus.AccessDenied => StepRunErrorCategory.Authentication,
            _ => StepRunErrorCategory.ConfigurationError,
        });
    }

    private JsonNode? Resolve(JsonNode? node, IReadOnlyDictionary<string, object?> data)
    {
        switch (node)
        {
            case JsonObject json:
                foreach (var (name, child) in json.ToList())
                {
                    json[name] = Resolve(child?.DeepClone(), data);
                }

                return json;
            case JsonArray array:
                return new JsonArray(array.Select(item => Resolve(item?.DeepClone(), data)).ToArray());
            case JsonValue value when value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } text && text.Contains("${"):
                // FUDGE (bindings): Automate resolves ${ … } only in string properties of a settings class marked with
                // its field attribute. The input is JSON, so the step resolves them itself, with Automate's public
                // evaluator.
                return WholeBinding().IsMatch(text)
                    ? ToNode(bindings.EvaluateRaw(text, data))
                    : JsonValue.Create(bindings.Evaluate(text, data));
            default:
                return node;
        }
    }

    [GeneratedRegex(@"^\s*\$\{[^}]*\}\s*$")]
    private static partial Regex WholeBinding();

    // FUDGE (output): ActionResult.Success(output) is internal to Automate. Only a subclass of its ActionBase can return
    // output, through a protected helper, and ActionBase reads the action's alias and form from a C# attribute and type,
    // so an action made at run time can't be one. This never-created subclass only reaches the helper.
    private abstract class Output : ActionBase<AbilitySettings, JsonNode>
    {
        private Output()
            : base(null!)
        {
        }

        public static new ActionResult Success(JsonNode output) => ActionBase<AbilitySettings, JsonNode>.Success(output);
    }
}

/// <summary>What a step's calls came to: the last call's output, or why they failed.</summary>
public sealed record StepCall(bool Success, AbilityOperationStatus Status, object? Output, string? Message = null, IReadOnlyList<Problem>? Problems = null)
{
    public static StepCall From(AbilityToolResult result)
        => new(result.Success, result.Status, result.Result, result.Message, result.Problems);

    public static StepCall Fail(AbilityOperationStatus status, string? message, IReadOnlyList<Problem>? problems = null)
        => new(false, status, null, message, problems);
}
