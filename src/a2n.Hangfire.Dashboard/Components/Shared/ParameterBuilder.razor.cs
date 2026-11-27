using Microsoft.AspNetCore.Components;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using a2n.Hangfire.Dashboard.Internal;

namespace a2n.Hangfire.Dashboard.Components.Shared;

public partial class ParameterBuilder
{
    /// <summary>The method whose Job_Parameters drive the form. When null, nothing is rendered.</summary>
    [Parameter] public JobMethodDescriptor? SelectedMethod { get; set; }

    /// <summary>
    /// Optional initial Parameter_JSON used to pre-fill the form (e.g. when editing an existing job).
    /// When absent or not interpretable for the selected method, the form starts at its defaults.
    /// </summary>
    [Parameter] public string? InitialParameterJson { get; set; }

    /// <summary>Raised whenever the captured Argument_Values, Parameter_JSON, or validity changes (Req 8.13).</summary>
    [Parameter] public EventCallback<ParameterState> OnParametersChanged { get; set; }

    private readonly List<FieldModel> _fields = new();
    private JobMethodDescriptor? _lastMethod;
    private string _parameterJson = "[]";
    private string _prettyJson = "[]";
    private bool _isValid = true;

    // Form/JSON mode state (Req 9). The component defaults to Form mode for each newly selected
    // method (9.1); _editorJson holds the editable textarea content while in JSON mode (9.3, 9.4);
    // _jsonError carries the constraint-specific message when a JSON→Form switch is blocked (9.6).
    private bool _jsonMode;
    private string _editorJson = "[]";
    private string? _jsonError = "";

    protected override void OnParametersSet()
    {
        // Rebuild the form when the selected method changes (Req 6.8 — the picker replaces inputs).
        if (!ReferenceEquals(_lastMethod, SelectedMethod))
        {
            _lastMethod = SelectedMethod;
            // A newly displayed method starts in Form mode with no pending JSON error (Req 9.1).
            _jsonMode = false;
            _jsonError = "";
            BuildFields();
            PopulateFromInitialJson();
            EmitState();
            // Seed the editor with the current canonical Parameter_JSON so an immediate switch to
            // JSON mode shows the live values (Req 9.4).
            _editorJson = _parameterJson;
        }
    }

    // --- Form/JSON mode toggle (Req 9) -------------------------------------------------------

    // Switch from JSON mode to Form mode (Req 9.5, 9.6). The edited Parameter_JSON is repopulated
    // into the form ONLY when it is *structurally* valid for the selected method — well-formed
    // JSON, an array of the right length, and each present value convertible to its parameter's
    // declared type. Otherwise JSON mode stays active, the content is preserved unchanged, and a
    // constraint-specific error is shown.
    //
    // A required parameter left blank (JSON null) does NOT block this switch: leaving a required
    // field empty while still shaping the rest of the job is normal mid-edit state, not a switch-
    // blocking error. Required-ness is enforced only at submit time (JobBuilder.Submit reads the
    // overall ParameterState.IsValid, which still reflects blank required fields via FieldValid),
    // so the operator can freely move between Form and JSON while filling the form out.
    private void SwitchToFormMode()
    {
        if (!_jsonMode)
        {
            return; // already in Form mode
        }

        var (valid, error) = ValidateJsonForMethod(_editorJson, requireAllPresent: false);
        if (!valid)
        {
            _jsonError = error;
            return; // stay in JSON mode, preserve the edited content (Req 9.6)
        }

        // Repopulate the form from the validated JSON so Argument_Values equal the JSON content
        // (Req 9.5), reusing the same build + apply path the initial pre-fill uses.
        BuildFields();
        PopulateFrom(_editorJson);
        _jsonMode = false;
        _jsonError = "";
        EmitState();
    }

    // Switch from Form mode to JSON mode (Req 9.3, 9.4). The editable textarea is initialized by
    // serializing the current Argument_Values into the canonical Parameter_JSON and retains that
    // content until the operator edits it.
    private void SwitchToJsonMode()
    {
        if (_jsonMode)
        {
            return; // already in JSON mode
        }

        _editorJson = _parameterJson;
        _jsonError = "";
        _jsonMode = true;
    }

    // The operator edits the raw Parameter_JSON. Store it verbatim, clear any stale switch error,
    // and emit the current state from the edited JSON so the parent stays in sync while in JSON mode.
    private void OnEditorChanged(string? value)
    {
        _editorJson = value ?? "";
        _jsonError = "";
        EmitJsonState();
    }

    // Validity of a Parameter_JSON for the selected method (Req 9.6): well-formed JSON, a top-level
    // array whose length equals the method's Job_Parameter count, and each *present* value
    // convertible to its parameter's declared type. Type convertibility is decided with the same
    // conversion the form uses (ApplyElement + FieldValid), so Form and JSON modes agree on what
    // is structurally acceptable.
    //
    // <paramref name="requireAllPresent"/> controls whether a blank (JSON null) required parameter
    // also fails validation:
    //   - true  (submit-time / EmitJsonState's reported validity): a required parameter must be
    //     present, matching the Form-mode submit gate (Req 8.15).
    //   - false (the JSON→Form switch gate): a required parameter may stay blank — switching
    //     views must not force the operator to finish a field they haven't gotten to yet.
    private (bool valid, string? error) ValidateJsonForMethod(string json, bool requireAllPresent = true)
    {
        var descriptors = SelectedMethod?.JobParameters ?? Array.Empty<JobParameterDescriptor>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? string.Empty : json);
        }
        catch (JsonException)
        {
            return (false, "The JSON is malformed. Enter a well-formed JSON array.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return (false, "The parameter input must be a JSON array.");
            }

            var elements = new List<JsonElement>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                elements.Add(element);
            }

            if (elements.Count != descriptors.Count)
            {
                return (false, $"Expected {descriptors.Count} argument(s) for this method but found {elements.Count}.");
            }

            // Apply each element to a throwaway set of fields and reuse the form's own validity rules.
            var temp = BuildFieldList();
            for (var i = 0; i < temp.Count; i++)
            {
                ApplyElement(temp[i], elements[i]);
            }

            foreach (var field in temp)
            {
                if (field.Invalid)
                {
                    return (false, $"Parameter '{field.Name}': {field.Error}");
                }

                var missingRequired = field.IsRequired && IsEmptySlot(field);
                if (missingRequired)
                {
                    // A blank required field is a structurally valid (but incomplete) slot: only
                    // fail on it when the caller demands full completeness (submit-time), not when
                    // merely checking whether the JSON is well-formed enough to redraw the form.
                    if (requireAllPresent)
                    {
                        return (false, $"Parameter '{field.Name}' is required.");
                    }

                    continue;
                }

                if (!FieldValid(field))
                {
                    return (false, $"The value for parameter '{field.Name}' is not valid for type {FriendlyType(field.DeclaredType)}.");
                }
            }

            return (true, null);
        }
    }

    // Emit the parent state derived from the edited JSON while in JSON mode. Unlike the JSON→Form
    // switch gate, this reports full validity — including required-ness — because it feeds the
    // parent's submit gate (JobBuilder.Submit checks ParameterState.IsValid): the operator must
    // still fill every required parameter before submitting, even while staying in JSON mode. The
    // elements are surfaced when the JSON is a well-formed array so the parent can read the current
    // Argument_Values regardless of the active mode.
    private void EmitJsonState()
    {
        var (valid, _) = ValidateJsonForMethod(_editorJson, requireAllPresent: true);

        var elements = new List<JsonElement>();
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(_editorJson) ? string.Empty : _editorJson);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    elements.Add(element.Clone());
                }
            }
        }
        catch (JsonException)
        {
            // Malformed JSON surfaces as an empty value set with validity false.
        }

        OnParametersChanged.InvokeAsync(new ParameterState(elements, _editorJson, valid));
    }

    // --- Form construction -------------------------------------------------------------------

    private void BuildFields()
    {
        _fields.Clear();
        _fields.AddRange(BuildFieldList());
    }

    // Builds a fresh list of top-level fields from the selected method's Job_Parameters. Used both
    // to (re)build the live form and to construct throwaway fields for JSON validity checks.
    private List<FieldModel> BuildFieldList()
    {
        var list = new List<FieldModel>();
        if (SelectedMethod is null)
        {
            return list;
        }

        foreach (var descriptor in SelectedMethod.JobParameters)
        {
            var field = new FieldModel
            {
                Name = descriptor.Name,
                DeclaredType = descriptor.DeclaredType,
                Kind = descriptor.InputKind,
                IsNullable = descriptor.IsNullable,
                IsRequired = descriptor.IsRequired,
                Depth = 1,
            };
            ConfigureCompositeMetadata(field);
            list.Add(field);
        }

        return list;
    }

    // Builds a field for a nested property or an array element, computing its kind from the same
    // ParameterInputMapper used for top-level parameters (Req 8.10 — same type-to-input mapping).
    private static FieldModel BuildField(string name, Type? type, int depth, bool required)
    {
        var field = new FieldModel
        {
            Name = name,
            DeclaredType = type,
            Kind = ParameterInputMapper.Map(type, depth),
            IsNullable = JobArgumentConverter.IsNullableType(type),
            IsRequired = required,
            Depth = depth,
        };
        ConfigureCompositeMetadata(field);
        return field;
    }

    private static void ConfigureCompositeMetadata(FieldModel field)
    {
        if (field.Kind == ParameterInputKind.ScalarArray)
        {
            field.ArrayElementType = field.DeclaredType!.GetElementType();
        }
    }

    // A nested object's sub-form is built only when the operator instantiates it (Req 8.10).
    private static List<FieldModel> BuildChildren(Type? type, int depth)
    {
        var children = new List<FieldModel>();
        var properties = type!.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0);

        foreach (var property in properties)
        {
            children.Add(BuildField(property.Name, property.PropertyType, depth, required: false));
        }

        return children;
    }

    private IReadOnlyList<string> EnumMembers(FieldModel field)
    {
        var enumType = Nullable.GetUnderlyingType(field.DeclaredType!) ?? field.DeclaredType!;
        return enumType.IsEnum ? Enum.GetNames(enumType) : Array.Empty<string>();
    }

    // --- Value capture handlers --------------------------------------------------------------

    private void OnScalarChanged(FieldModel field, string? value)
    {
        field.Text = value ?? "";
        CommitScalar(field);
        EmitState();
    }

    private void OnBoolChanged(FieldModel field, bool value)
    {
        field.Bool = value;
        EmitState();
    }

    private void OnTriStateChanged(FieldModel field, string? value)
    {
        field.TriState = value ?? "";
        EmitState();
    }

    private void OnEnumSingleChanged(FieldModel field, string? value)
    {
        field.EnumValue = value ?? "";
        EmitState();
    }

    private void OnEnumFlagToggled(FieldModel field, string member, bool selected)
    {
        if (selected)
        {
            field.EnumFlags.Add(member);
        }
        else
        {
            field.EnumFlags.Remove(member);
        }

        EmitState();
    }

    private void AddArrayItem(FieldModel field)
    {
        var item = BuildField($"Item {field.ArrayItems.Count + 1}", field.ArrayElementType, field.Depth, required: false);
        field.ArrayItems.Add(item);
        EmitState();
    }

    private void RemoveArrayItem(FieldModel field, FieldModel item)
    {
        field.ArrayItems.Remove(item);
        EmitState();
    }

    private void InstantiateNested(FieldModel field)
    {
        field.Instantiated = true;
        field.Children = BuildChildren(field.DeclaredType, field.Depth + 1);
        EmitState();
    }

    private void ClearNested(FieldModel field)
    {
        field.Instantiated = false;
        field.Children.Clear();
        EmitState();
    }

    // Validates a non-empty scalar text value; a blank value is the empty case and resolves to JSON
    // null (always valid). On an invalid non-empty value the prior committed value is preserved
    // unchanged and the field is flagged invalid (Req 8.14, 8.15).
    private static void CommitScalar(FieldModel field)
    {
        var raw = field.Text;
        if (string.IsNullOrWhiteSpace(raw))
        {
            field.CommittedJson = "null";
            field.Invalid = false;
            field.Error = "";
            return;
        }

        var (ok, json, error) = TryScalarToJson(raw, field.Kind, field.DeclaredType);
        if (ok)
        {
            field.CommittedJson = json;
            field.Invalid = false;
            field.Error = "";
        }
        else
        {
            // Reject: keep the prior valid value (CommittedJson) unchanged (Req 8.14).
            field.Invalid = true;
            field.Error = error;
        }
    }

    // Converts a non-empty scalar string to its canonical JSON token, or reports why it is invalid.
    private static (bool ok, string? json, string? error) TryScalarToJson(string raw, ParameterInputKind kind, Type? declaredType)
    {
        var underlying = Nullable.GetUnderlyingType(declaredType!) ?? declaredType!;
        var inv = CultureInfo.InvariantCulture;

        switch (kind)
        {
            case ParameterInputKind.Text:
                if (raw.Length > 4000)
                {
                    return (false, null, "Text must be 4000 characters or fewer.");
                }

                return (true, JsonSerializer.Serialize(raw), null);

            case ParameterInputKind.Integer:
                try
                {
                    // Convert.ChangeType rejects fractional input (FormatException) and out-of-range
                    // values (OverflowException) for the declared integral type (Req 8.3, 8.14).
                    var value = Convert.ChangeType(raw, underlying, inv);
                    return (true, Convert.ToString(value, inv), null);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
                {
                    return (false, null, $"Enter a whole number within the range of {FriendlyType(underlying)}.");
                }

            case ParameterInputKind.Float:
                try
                {
                    var value = Convert.ChangeType(raw, underlying, inv);
                    return (true, FormatNumber(value), null);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
                {
                    return (false, null, $"Enter a valid {FriendlyType(underlying)} value.");
                }

            case ParameterInputKind.Guid:
                return Guid.TryParse(raw, out var guid)
                    ? (true, JsonSerializer.Serialize(guid.ToString()), null)
                    : (false, null, "Enter a valid GUID (e.g. 00000000-0000-0000-0000-000000000000).");

            case ParameterInputKind.Date:
                return DateOnly.TryParse(raw, inv, DateTimeStyles.None, out _)
                    ? (true, JsonSerializer.Serialize(raw), null)
                    : (false, null, "Enter a valid date.");

            case ParameterInputKind.Time:
                return TimeOnly.TryParse(raw, inv, DateTimeStyles.None, out _)
                    ? (true, JsonSerializer.Serialize(raw), null)
                    : (false, null, "Enter a valid time.");

            case ParameterInputKind.DateTime:
                var dateTimeOk = underlying == typeof(DateTimeOffset)
                    ? DateTimeOffset.TryParse(raw, inv, DateTimeStyles.RoundtripKind, out _)
                    : DateTime.TryParse(raw, inv, DateTimeStyles.RoundtripKind, out _);
                return dateTimeOk
                    ? (true, JsonSerializer.Serialize(raw), null)
                    : (false, null, "Enter a valid date and time.");

            case ParameterInputKind.Json:
                try
                {
                    using var _ = JsonDocument.Parse(raw);
                    return (true, raw, null);
                }
                catch (JsonException)
                {
                    return (false, null, "Enter syntactically valid JSON.");
                }

            default:
                // Non-scalar kinds are handled by their own controls, not this path.
                return (true, JsonSerializer.Serialize(raw), null);
        }
    }

    private static string? FormatNumber(object value) => value switch
    {
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static string FriendlyType(Type? type) => type!.Name;

    // --- Canonical JSON + validity -----------------------------------------------------------

    // The canonical Parameter_JSON token for a single field. Empty/unset fields and unselected
    // choices emit JSON null (the canonical encoding of a blank, Req 8.15); composites recurse.
    private static string SlotJson(FieldModel field) => field.Kind switch
    {
        ParameterInputKind.Bool => field.Bool ? "true" : "false",
        ParameterInputKind.NullableBool => field.TriState.Length == 0 ? "null" : field.TriState,
        ParameterInputKind.EnumSingle => field.EnumValue!.Length == 0 ? "null" : JsonSerializer.Serialize(field.EnumValue),
        ParameterInputKind.EnumFlags => field.EnumFlags.Count == 0 ? "null" : JsonSerializer.Serialize(string.Join(", ", field.EnumFlags)),
        ParameterInputKind.ScalarArray => "[" + string.Join(",", field.ArrayItems.Select(SlotJson)) + "]",
        ParameterInputKind.NestedObject => field.Instantiated
            ? "{" + string.Join(",", field.Children.Select(c => JsonSerializer.Serialize(c.Name) + ":" + SlotJson(c))) + "}"
            : "null",
        _ => string.IsNullOrEmpty(field.CommittedJson) ? "null" : field.CommittedJson,
    };

    private static bool IsEmptySlot(FieldModel field) => SlotJson(field) == "null";

    private static bool FieldValid(FieldModel field)
    {
        if (field.Invalid)
        {
            return false;
        }

        if (field.Kind == ParameterInputKind.ScalarArray && !field.ArrayItems.All(FieldValid))
        {
            return false;
        }

        if (field.Kind == ParameterInputKind.NestedObject && field.Instantiated && !field.Children.All(FieldValid))
        {
            return false;
        }

        // A required parameter with no entered value is excluded from a successful result (Req 8.15).
        if (field.IsRequired && IsEmptySlot(field))
        {
            return false;
        }

        return true;
    }

    private static bool ShowRequired(FieldModel field) =>
        field.IsRequired && !field.Invalid && IsEmptySlot(field);

    private void EmitState()
    {
        var slots = _fields.Select(SlotJson).ToList();
        _parameterJson = "[" + string.Join(",", slots) + "]";

        var elements = new List<JsonElement>(_fields.Count);
        try
        {
            using var doc = JsonDocument.Parse(_parameterJson);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                elements.Add(element.Clone());
            }

            _prettyJson = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            _prettyJson = _parameterJson;
        }

        _isValid = _fields.All(FieldValid);

        OnParametersChanged.InvokeAsync(new ParameterState(elements, _parameterJson, _isValid));
    }

    // --- Initial JSON pre-fill ---------------------------------------------------------------

    private void PopulateFromInitialJson()
    {
        PopulateFrom(InitialParameterJson);
    }

    // Applies a Parameter_JSON array onto the current form fields, in order. Used for the initial
    // edit pre-fill and for the JSON→Form repopulation when the edited JSON is valid (Req 9.5).
    private void PopulateFrom(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var index = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (index >= _fields.Count)
                {
                    break;
                }

                ApplyElement(_fields[index], element);
                index++;
            }
        }
        catch (JsonException)
        {
            // Uninterpretable JSON leaves the form at its defaults.
        }
    }

    private static void ApplyElement(FieldModel field, JsonElement element)
    {
        try
        {
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return; // unset / blank
            }

            switch (field.Kind)
            {
                case ParameterInputKind.Bool:
                    field.Bool = element.ValueKind == JsonValueKind.True
                        || (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var b) && b);
                    break;

                case ParameterInputKind.NullableBool:
                    field.TriState = element.GetBoolean() ? "true" : "false";
                    break;

                case ParameterInputKind.EnumSingle:
                    field.EnumValue = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
                    break;

                case ParameterInputKind.EnumFlags:
                    var raw = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
                    foreach (var part in raw!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        field.EnumFlags.Add(part);
                    }
                    break;

                case ParameterInputKind.ScalarArray:
                    if (element.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in element.EnumerateArray())
                        {
                            var itemField = BuildField($"Item {field.ArrayItems.Count + 1}", field.ArrayElementType, field.Depth, required: false);
                            ApplyElement(itemField, item);
                            field.ArrayItems.Add(itemField);
                        }
                    }
                    break;

                case ParameterInputKind.NestedObject:
                    if (element.ValueKind == JsonValueKind.Object)
                    {
                        field.Instantiated = true;
                        field.Children = BuildChildren(field.DeclaredType, field.Depth + 1);
                        foreach (var child in field.Children)
                        {
                            if (element.TryGetProperty(child.Name, out var childElement))
                            {
                                ApplyElement(child, childElement);
                            }
                        }
                    }
                    break;

                default:
                    // Scalar text-bound kinds: seed the editor text and re-validate.
                    field.Text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
                    CommitScalar(field);
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            // Best-effort pre-fill: an element that does not fit the field is left at its default.
        }
    }


    // --- Models --------------------------------------------------------------------------------

    /// <summary>
    /// The mutable per-parameter (or nested-property / array-element) form state. Leaf scalar kinds
    /// store their last valid canonical JSON token in <see cref="CommittedJson"/>; choice and
    /// composite kinds derive their JSON from their structured state via <c>SlotJson</c>.
    /// </summary>
    private sealed class FieldModel
    {
        public string Name { get; set; } = "";
        public Type? DeclaredType { get; set; }
        public ParameterInputKind Kind { get; set; }
        public bool IsNullable { get; set; }
        public bool IsRequired { get; set; }
        public int Depth { get; set; } = 1;

        // Editor text for the scalar text-bound kinds (Text/Integer/Float/Guid/Date/Time/DateTime/Json).
        public string? Text { get; set; } = "";

        // Last valid canonical JSON token for the text-bound kinds; "null" represents a blank value.
        public string? CommittedJson { get; set; } = "null";

        // Validation state for a non-empty value that failed conversion (Req 8.14).
        public bool Invalid { get; set; }
        public string? Error { get; set; } = "";

        // Choice-control state.
        public bool Bool { get; set; }
        public string TriState { get; set; } = "";   // "", "true", "false"
        public string? EnumValue { get; set; } = "";
        public HashSet<string> EnumFlags { get; } = new();

        // Composite-control state.
        public Type? ArrayElementType { get; set; }
        public List<FieldModel> ArrayItems { get; } = new();
        public bool Instantiated { get; set; }
        public List<FieldModel> Children { get; set; } = new();
    }

    /// <summary>
    /// The Parameter Builder's output to its parent: the current Argument_Values (ordered over the
    /// method's Job_Parameters), the canonical Parameter_JSON, and whether the captured values are
    /// valid (no invalid non-empty input and no missing required value).
    /// </summary>
    /// <param name="ArgumentValues">Current values as JSON elements, one per Job_Parameter, in order.</param>
    /// <param name="ParameterJson">Canonical Parameter_JSON array string.</param>
    /// <param name="IsValid">Whether the captured values are valid for submission.</param>
    public sealed record ParameterState(
        IReadOnlyList<JsonElement> ArgumentValues,
        string ParameterJson,
        bool IsValid);
}
