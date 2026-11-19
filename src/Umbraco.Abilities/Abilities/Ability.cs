using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.Entities;

namespace Umbraco.Abilities;

/// <summary>What an ability does. Products derive retry, approval and MCP hints from it.</summary>
public enum AbilityKind
{
    Explain,
    Read,
    Transform,
    Trigger,
    Write,
}

/// <summary>Declares a class as an ability.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AbilityAttribute(string id, string name) : Attribute
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    /// <summary>
    /// What it does, for every reader, model or person. How to use it, for one of them, goes in a
    /// <see cref="HintAttribute"/> on the class.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    public AbilityKind Kind { get; set; } = AbilityKind.Read;

    /// <summary>
    /// A Write that changes no live data, or whose change can be undone (recording into a change set,
    /// creating or discarding a set). Approval is only needed for a Write that is not reversible.
    /// </summary>
    public bool Reversible { get; set; }

    /// <summary>
    /// For a side effect that may fail for a while (a mail server, an external API): applied in a change set, it runs in
    /// the background once every change is applied, retried up to this many times with backoff. Only failures are
    /// retried, never correctable input or a refusal. Zero runs it inline, once.
    /// </summary>
    public int Retries { get; set; }

    /// <summary>
    /// False for an ability whose result can't depend on a change set, such as describing a schema. It always runs
    /// outside a set: its tool takes no change set id, and needs none named while several sets are open. Only reads,
    /// and the change set tools that start a set, can say false: any other Write would run on live data.
    /// </summary>
    public bool UsesChangeSet { get; set; } = true;

    /// <summary>
    /// True for a building block: one small edit of many that together build or change an entity, such as setting one
    /// value by its path. A model chains these. A surface that offers whole operations to a person, such as a workflow
    /// step or a quick action, leaves them out and offers the whole operation instead.
    /// </summary>
    public bool Granular { get; set; }

    /// <summary>
    /// What a consumer must be able to do to offer it, by name (<see cref="Consumers.ConsumerCapabilities"/>), such as
    /// <c>acme.file-upload</c> for an ability that takes a file. The base policy leaves it out where one is missing.
    /// </summary>
    public string[] Needs { get; set; } = [];

    /// <summary>
    /// An argument a consumer may split the ability by: one tool per value, with the value fixed, such as one "Create"
    /// per entity type. The ability lists the values (<see cref="IDynamicInputAbility"/>). A consumer that splits
    /// applies the <c>split-by-choice</c> filter; one that doesn't sees one tool, as before.
    /// </summary>
    public string? SplitBy { get; set; }

    /// <summary>The title of each tool a split makes, with <c>{0}</c> for the value's name, such as <c>"Create {0}"</c>.</summary>
    public string? SplitTitle { get; set; }

    /// <summary>
    /// An input property whose schema depends on other arguments (<see cref="DependsOn"/>), such as the fields of the
    /// chosen subtype. The ability gives its schema once they are chosen (<see cref="IDynamicInputAbility"/>).
    /// </summary>
    public string? DependentInput { get; set; }

    /// <summary>The arguments <see cref="DependentInput"/>'s schema depends on, in the order a person chooses them.</summary>
    public string[] DependsOn { get; set; } = [];
}

/// <summary>
/// Marks a Guid as the key of an entity of the given type, or an <c>EntityReference</c> as pointing at one of several
/// entity types.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class EntityKeyAttribute(params string[] entityTypes) : Attribute
{
    public IReadOnlyList<string> EntityTypes { get; } = entityTypes;

    public string EntityType => EntityTypes[0];
}

/// <summary>
/// The facts an ability states about itself, from its <see cref="AbilityAttribute"/>. Consumers' filters read them to
/// decide what to offer; an ability never names a consumer.
/// </summary>
/// <param name="ManagesChangeSets">
/// True for an ability that creates, applies or discards change sets itself (<see cref="ChangeSets.IChangeSetLifecycleAbility"/>).
/// It is never recorded into a set, and only a consumer that keeps a conversation open can offer it.
/// </param>
/// <param name="Hints">
/// How to use the ability, by audience (<c>model</c>, <c>person</c>), from <see cref="HintAttribute"/>s on its class.
/// The description says what it does, for every reader; the <c>apply-hints</c> filter adds the reader's hint to it.
/// </param>
/// <param name="Input">How the input can change with what is chosen: see <see cref="AbilityInputFacts"/>.</param>
/// <param name="Needs">What a consumer must be able to do to offer it, by name. See <see cref="AbilityAttribute.Needs"/>.</param>
public sealed record AbilityDescriptor(
    string Id,
    string Name,
    string Description,
    AbilityKind Kind,
    bool Reversible,
    Type InputType,
    int Retries = 0,
    bool UsesChangeSet = true,
    Type? OutputType = null,
    bool Granular = false,
    bool ManagesChangeSets = false,
    IReadOnlyDictionary<string, string>? Hints = null,
    AbilityInputFacts? Input = null,
    IReadOnlyList<string>? Needs = null)
{
    /// <summary>What a consumer must be able to do to offer it. Never null: an ability that states nothing needs nothing.</summary>
    public IReadOnlyList<string> Needs { get; init; } = Needs ?? [];

    /// <summary>How the input can change with what is chosen. Never null: an ability that states nothing has no facts.</summary>
    public AbilityInputFacts Input { get; init; } = Input ?? AbilityInputFacts.None;

    public bool RequiresApproval => Kind == AbilityKind.Write && !Reversible;
}

/// <summary>
/// How an ability's input can change with what is chosen, from its <see cref="AbilityAttribute"/>.
/// </summary>
/// <param name="SplitBy">The argument a consumer may split the ability by, one tool per value. See <see cref="AbilityAttribute.SplitBy"/>.</param>
/// <param name="SplitTitle">Each split tool's title, with <c>{0}</c> for the value's name.</param>
/// <param name="DependentInput">The input property whose schema depends on <paramref name="DependsOn"/>.</param>
/// <param name="DependsOn">The arguments the dependent input depends on.</param>
public sealed record AbilityInputFacts(string? SplitBy, string? SplitTitle, string? DependentInput, IReadOnlyList<string> DependsOn)
{
    /// <summary>No split and no dependent input.</summary>
    public static AbilityInputFacts None { get; } = new(null, null, null, []);
}

/// <summary>A value a person or model can choose for an argument, such as an entity type or a page type.</summary>
public sealed record AbilityChoice(string Value, string Name);

/// <summary>
/// An ability whose input changes with what is chosen: it lists the values an argument can take (to split by it, or to
/// pick from), and gives its input schema once some arguments are chosen. Both are asked for each time, so a value
/// added later, such as a new page type, is offered without a restart.
/// </summary>
public interface IDynamicInputAbility
{
    /// <summary>The values an argument can take, given what is already chosen. Empty when nothing can be chosen yet.</summary>
    Task<IReadOnlyList<AbilityChoice>> GetChoicesAsync(string argument, JsonObject chosen, CancellationToken cancellationToken = default);

    /// <summary>
    /// The input schema, given what is chosen: the choices as <c>oneOf</c> lists, and the dependent input's schema
    /// once what it depends on is chosen. Hints are left in (<c>x-hints</c>) for each consumer's filters.
    /// </summary>
    Task<JsonElement> ResolveInputSchemaAsync(JsonObject chosen, CancellationToken cancellationToken = default);

    /// <summary>
    /// The dependent input's values as set with each field's editor (<c>x-editor</c>), read as the values the ability
    /// takes. A form sends editor values; a model sends the values themselves. Values in <paramref name="bound"/> came
    /// from an earlier step, so they are values already, and kept as they are.
    /// </summary>
    Task<JsonObject> FromEditorValuesAsync(JsonObject chosen, JsonObject values, ISet<string> bound, CancellationToken cancellationToken = default);
}

/// <summary>
/// Who is calling: a backoffice user's key, as the CMS's services take it. An agent acts for a user: it gets that user's
/// permissions, no more. Which change set the call works in is ambient, from <c>IChangeSetAccessor</c>.
/// </summary>
public sealed record AbilityContext(Guid UserKey);

/// <summary>
/// How an ability call ended. Mirrors the CMS's operation status enums (e.g. <c>ContentEditingOperationStatus</c>).
/// </summary>
public enum AbilityOperationStatus
{
    Success,

    /// <summary>The input was wrong. The caller can correct it and try again.</summary>
    InvalidInput,

    /// <summary>The acting user may not do this.</summary>
    AccessDenied,

    /// <summary>Something failed while running. Retrying the same input will not help.</summary>
    Failed,

    /// <summary>
    /// It can't be undone, and the surface didn't say the person agreed to this call. Nothing was done. The surface asks
    /// the person, then calls again with their consent.
    /// </summary>
    ApprovalRequired,
}

/// <summary>
/// A problem at a path. Mirrors the CMS's <c>PropertyValidationError</c>: error messages per variant (its culture and
/// segment are two dimensions of <see cref="Variant"/>), at a path. The path is the entity path the edit abilities take (an alias, or a path into items), where the CMS splits it
/// into an alias and a JSON path into the value.
/// </summary>
public sealed record Problem(string Path, IReadOnlyList<string> ErrorMessages)
{
    public Problem(string path, string message)
        : this(path, [message])
    {
    }

    /// <summary>The variant the problem is in, e.g. <c>{ "culture": "da-DK" }</c>; null for a value that doesn't vary.</summary>
    public IReadOnlyDictionary<string, string>? Variant { get; init; }

    public Problem In(VariantId variant) => this with { Variant = variant.ToJson() };

    /// <summary>The error messages as one sentence.</summary>
    public override string ToString() => string.Join(" ", ErrorMessages);
}

/// <summary>
/// The result of an ability call. Mirrors the CMS's <c>Attempt&lt;TResult, TStatus&gt;</c>: a status and a result, plus a
/// message and problems when it failed.
/// </summary>
public sealed record AbilityResult(AbilityOperationStatus Status, object? Result, string? Message = null, IReadOnlyList<Problem>? Problems = null)
{
    public bool Success => Status == AbilityOperationStatus.Success;

    /// <summary>The caller can correct the input and try again.</summary>
    public bool Correctable => Status == AbilityOperationStatus.InvalidInput;

    public static AbilityResult Ok(object? result) => new(AbilityOperationStatus.Success, result);

    public static AbilityResult Fail(AbilityOperationStatus status, string message, IReadOnlyList<Problem>? problems = null)
        => new(status, null, message, problems ?? []);
}

/// <summary>
/// The standard way an ability points at an entity it created, changed or found. <c>Subtype</c> is its subtype, such as
/// a document's content type alias, for entity types that have them.
/// </summary>
public sealed record EntitySummary(string EntityType, Guid Key, string Name, string? EditUrl = null, string? Subtype = null);

public interface IAbility
{
    AbilityDescriptor Descriptor { get; }

    Task<AbilityResult> InvokeAsync(JsonObject input, AbilityContext context, CancellationToken cancellationToken = default);
}

/// <summary>Thrown inside an ability to report a correctable input problem.</summary>
public sealed class AbilityInputException(string message, IReadOnlyList<Problem>? problems = null) : Exception(message)
{
    public IReadOnlyList<Problem> Problems { get; } = problems ?? [];
}

public static class AbilityJson
{
    /// <summary>Shared, read-only options with a resolver, so schema generators can use them too.</summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>A stateless ability with a typed input and output.</summary>
public abstract class AbilityBase<TInput, TOutput> : IAbility
{
    protected AbilityBase()
    {
        var attribute = (AbilityAttribute?)Attribute.GetCustomAttribute(GetType(), typeof(AbilityAttribute))
            ?? throw new InvalidOperationException($"{GetType().Name} has no [Ability] attribute.");

        Descriptor = new AbilityDescriptor(
            attribute.Id,
            attribute.Name,
            attribute.Description,
            attribute.Kind,
            attribute.Reversible,
            typeof(TInput),
            attribute.Retries,
            attribute.UsesChangeSet,
            typeof(TOutput),
            attribute.Granular,
            ManagesChangeSets: this is ChangeSets.IChangeSetLifecycleAbility,
            Hints: UiHints.HintsOf(GetType()),
            Input: new AbilityInputFacts(attribute.SplitBy, attribute.SplitTitle, attribute.DependentInput, attribute.DependsOn),
            Needs: attribute.Needs);

        if ((attribute.SplitBy is not null || attribute.DependentInput is not null) && this is not IDynamicInputAbility)
        {
            throw new InvalidOperationException(
                $"{attribute.Id} splits by an argument or has a dependent input, so it must list choices and resolve its input: implement IDynamicInputAbility.");
        }

        if (!attribute.UsesChangeSet && attribute.Kind == AbilityKind.Write && !Descriptor.ManagesChangeSets)
        {
            throw new InvalidOperationException(
                $"{attribute.Id} is a Write, so it must use the change set: outside one it would change live data.");
        }
    }

    public AbilityDescriptor Descriptor { get; }

    public async Task<AbilityResult> InvokeAsync(JsonObject input, AbilityContext context, CancellationToken cancellationToken = default)
    {
        TInput? typed;
        try
        {
            typed = input.Deserialize<TInput>(AbilityJson.Options);
        }
        catch (JsonException ex)
        {
            return AbilityResult.Fail(AbilityOperationStatus.InvalidInput, $"The input does not match the schema: {ex.Message}");
        }

        if (typed is null)
        {
            return AbilityResult.Fail(AbilityOperationStatus.InvalidInput, "Input is required.");
        }

        try
        {
            return await ExecuteAsync(typed, context, cancellationToken);
        }
        catch (AbilityInputException ex)
        {
            return AbilityResult.Fail(AbilityOperationStatus.InvalidInput, ex.Message, ex.Problems);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AbilityResult.Fail(AbilityOperationStatus.Failed, ex.Message);
        }
    }

    protected abstract Task<AbilityResult> ExecuteAsync(TInput input, AbilityContext context, CancellationToken cancellationToken);

    protected static AbilityResult Success(TOutput output) => AbilityResult.Ok(output);
}

/// <summary>
/// The input of an ability that takes none. Callers send an empty object (<c>{}</c>). Derive from
/// <see cref="AbilityBase{TOutput}"/> rather than naming this type.
/// </summary>
public sealed record NoInput;

/// <summary>A stateless ability that takes no input, only a typed output. For example, one that works on the current change set.</summary>
public abstract class AbilityBase<TOutput> : AbilityBase<NoInput, TOutput>
{
    protected sealed override Task<AbilityResult> ExecuteAsync(NoInput input, AbilityContext context, CancellationToken cancellationToken)
        => ExecuteAsync(context, cancellationToken);

    protected abstract Task<AbilityResult> ExecuteAsync(AbilityContext context, CancellationToken cancellationToken);
}

/// <summary>Every ability a caller can be offered, by id.</summary>
public sealed class AbilityRegistry
{
    private readonly Dictionary<string, IAbility> _abilities = new(StringComparer.Ordinal);
    private readonly List<Func<IEnumerable<IAbility>>> _later = [];
    private volatile bool _laterAdded;
    private bool _addingLater;

    public AbilityRegistry Add(IAbility ability)
    {
        _abilities.Add(ability.Descriptor.Id, ability);
        return this;
    }

    /// <summary>
    /// Adds abilities the first time the registry is read, not now. For abilities that need a service which needs this
    /// registry, such as the change set service: they can't be made before it.
    /// </summary>
    public AbilityRegistry AddLater(Func<IEnumerable<IAbility>> abilities)
    {
        if (_laterAdded)
        {
            throw new InvalidOperationException("The registry has been read, so it can't take more abilities later.");
        }

        _later.Add(abilities);
        return this;
    }

    public IAbility? Find(string id)
    {
        AddLaterAbilities();
        return _abilities.GetValueOrDefault(id);
    }

    public IReadOnlyCollection<IAbility> All
    {
        get
        {
            AddLaterAbilities();
            return _abilities.Values;
        }
    }

    private void AddLaterAbilities()
    {
        if (_laterAdded || _later.Count == 0)
        {
            return;
        }

        lock (_later)
        {
            // An ability being made may read the registry. It sees the abilities added so far.
            if (_laterAdded || _addingLater)
            {
                return;
            }

            _addingLater = true;
            try
            {
                foreach (var ability in _later.SelectMany(abilities => abilities()))
                {
                    Add(ability);
                }

                _laterAdded = true;
            }
            finally
            {
                _addingLater = false;
            }
        }
    }
}
