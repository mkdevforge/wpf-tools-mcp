using System.Text.Json;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
public sealed class DiagnosticSnapshotToolSchemaTests
{
    private static readonly string[] ExpectedInputProperties =
    [
        "budget",
        "dataContextProperties",
        "elementId",
        "locator",
        "propertyNames",
        "sections",
        "sessionId",
        "timeoutMs",
        "windowHandle"
    ];

    private static readonly string[] ForbiddenFieldNames =
    [
        "expression",
        "method",
        "script",
        "steps"
    ];

    [Test]
    public async Task Core_profile_exposes_a_bounded_read_only_diagnostic_snapshot_schema()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(
            serverExe,
            toolProfile: "core",
            environmentVariables: new Dictionary<string, string?>
            {
                ["WPF_TOOLS_MCP_TOOL_PROFILE"] = null
            });

        var tool = (await mcp.ListToolsAsync())
            .Single(candidate => candidate.Name == "capture_diagnostic_snapshot");
        var root = tool.JsonSchema;
        var properties = RequireObjectProperty(root, "properties", "tool input properties");
        var propertyNames = properties
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var sections = RequireSchemaWithProperty(
            properties.GetProperty("sections"),
            "items",
            "sections array");
        var sectionItems = RequireSchemaWithProperty(
            sections.GetProperty("items"),
            "enum",
            "sections items");
        var sectionValues = sectionItems.GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Where(value => value is not null)
            .Select(value => value!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var budget = RequireSchemaWithProperty(
            properties.GetProperty("budget"),
            "properties",
            "budget object");
        var budgetProperties = RequireObjectProperty(budget, "properties", "budget properties");

        Assert.Multiple(() =>
        {
            Assert.That(propertyNames, Is.EqualTo(ExpectedInputProperties));
            Assert.That(ReadBoolean(root, "additionalProperties"), Is.False);

            Assert.That(ReadInt32(sections, "minItems"), Is.EqualTo(1));
            Assert.That(ReadInt32(sections, "maxItems"), Is.EqualTo(DiagnosticSnapshotLimits.MaxSections));
            Assert.That(ReadBoolean(sections, "uniqueItems"), Is.True);
            Assert.That(
                sectionValues,
                Is.EqualTo(Enum.GetNames<DiagnosticSection>().OrderBy(value => value, StringComparer.Ordinal)));

            Assert.That(
                budgetProperties.EnumerateObject().Select(property => property.Name).OrderBy(value => value, StringComparer.Ordinal),
                Is.EqualTo(new[] { "maxDepth", "maxItems", "maxNodes", "maxPayloadChars", "maxValueLength" }));
            Assert.That(ReadBoolean(budget, "additionalProperties"), Is.False);
            AssertIntegerBounds(
                budgetProperties.GetProperty("maxDepth"),
                DiagnosticSnapshotLimits.MinDepth,
                DiagnosticSnapshotLimits.MaxDepth,
                "budget.maxDepth");
            AssertIntegerBounds(
                budgetProperties.GetProperty("maxItems"),
                DiagnosticSnapshotLimits.MinItems,
                DiagnosticSnapshotLimits.MaxItems,
                "budget.maxItems");
            AssertIntegerBounds(
                budgetProperties.GetProperty("maxNodes"),
                DiagnosticSnapshotLimits.MinNodes,
                DiagnosticSnapshotLimits.MaxNodes,
                "budget.maxNodes");
            AssertIntegerBounds(
                budgetProperties.GetProperty("maxValueLength"),
                DiagnosticSnapshotLimits.MinValueLength,
                DiagnosticSnapshotLimits.MaxValueLength,
                "budget.maxValueLength");
            AssertIntegerBounds(
                budgetProperties.GetProperty("maxPayloadChars"),
                DiagnosticSnapshotLimits.MinPayloadChars,
                DiagnosticSnapshotLimits.MaxPayloadChars,
                "budget.maxPayloadChars");
            AssertIntegerBounds(
                properties.GetProperty("timeoutMs"),
                DiagnosticSnapshotLimits.MinTimeoutMs,
                DiagnosticSnapshotLimits.MaxTimeoutMs,
                "timeoutMs");

            AssertNameArrayBounds(properties.GetProperty("propertyNames"), "propertyNames");
            AssertNameArrayBounds(properties.GetProperty("dataContextProperties"), "dataContextProperties");
            AssertNonWhitespaceString(properties.GetProperty("elementId"), "elementId");

            Assert.That(
                ForbidsRequiredPropertiesTogether(root, "locator", "elementId"),
                Is.True,
                "The schema must reject requests that provide both locator and elementId.");
            Assert.That(
                RequiresPropertyForSection(root, "WpfProperties", "propertyNames"),
                Is.True,
                "WpfProperties must require propertyNames.");
            Assert.That(
                RequiresSectionForProperty(root, "propertyNames", "WpfProperties"),
                Is.True,
                "propertyNames must only be accepted with WpfProperties.");
            Assert.That(
                RequiresSectionForProperty(root, "dataContextProperties", "DataContext"),
                Is.True,
                "dataContextProperties must only be accepted with DataContext.");

            var exposedFields = EnumerateExposedPropertyNames(root).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.That(
                exposedFields.Intersect(ForbiddenFieldNames, StringComparer.OrdinalIgnoreCase),
                Is.Empty);
        });
    }

    private static void AssertIntegerBounds(JsonElement schema, int minimum, int maximum, string label)
    {
        var integer = RequireSchemaAllowingType(schema, "integer", label);
        Assert.Multiple(() =>
        {
            Assert.That(ReadInt32(integer, "minimum"), Is.EqualTo(minimum), $"{label} minimum");
            Assert.That(ReadInt32(integer, "maximum"), Is.EqualTo(maximum), $"{label} maximum");
        });
    }

    private static void AssertNameArrayBounds(JsonElement schema, string label)
    {
        var array = RequireSchemaWithProperty(schema, "items", $"{label} array");
        var items = RequireSchemaAllowingType(array.GetProperty("items"), "string", $"{label} items");

        Assert.Multiple(() =>
        {
            Assert.That(ReadInt32(array, "minItems"), Is.EqualTo(1));
            Assert.That(ReadInt32(array, "maxItems"), Is.EqualTo(DiagnosticSnapshotLimits.MaxPropertyNames));
            Assert.That(ReadBoolean(array, "uniqueItems"), Is.True);
            Assert.That(ReadInt32(items, "minLength"), Is.EqualTo(1));
            Assert.That(ReadInt32(items, "maxLength"), Is.EqualTo(DiagnosticSnapshotLimits.MaxPropertyNameLength));
            Assert.That(items.GetProperty("pattern").GetString(), Is.EqualTo("\\S"));
        });
    }

    private static void AssertNonWhitespaceString(JsonElement schema, string label)
    {
        var value = RequireSchemaAllowingType(schema, "string", label);
        Assert.Multiple(() =>
        {
            Assert.That(ReadInt32(value, "minLength"), Is.EqualTo(1));
            Assert.That(value.GetProperty("pattern").GetString(), Is.EqualTo("\\S"));
        });
    }

    private static bool RequiresPropertyForSection(
        JsonElement root,
        string section,
        string propertyName) =>
        EnumerateAllOf(root).Any(rule =>
            TryReadContainedSection(rule, "if", out var requiredSection) &&
            string.Equals(requiredSection, section, StringComparison.Ordinal) &&
            TryReadRequiredProperty(rule, "then", out var requiredProperty) &&
            string.Equals(requiredProperty, propertyName, StringComparison.Ordinal));

    private static bool RequiresSectionForProperty(
        JsonElement root,
        string propertyName,
        string section) =>
        EnumerateAllOf(root).Any(rule =>
            TryReadRequiredProperty(rule, "if", out var requiredProperty) &&
            string.Equals(requiredProperty, propertyName, StringComparison.Ordinal) &&
            TryReadContainedSection(rule, "then", out var requiredSection) &&
            string.Equals(requiredSection, section, StringComparison.Ordinal));

    private static IEnumerable<JsonElement> EnumerateAllOf(JsonElement root) =>
        root.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array
            ? allOf.EnumerateArray().ToArray()
            : [];

    private static bool TryReadContainedSection(
        JsonElement rule,
        string phase,
        out string? section)
    {
        section = null;
        return rule.TryGetProperty(phase, out var condition) &&
               condition.TryGetProperty("properties", out var properties) &&
               properties.TryGetProperty("sections", out var sections) &&
               sections.TryGetProperty("contains", out var contains) &&
               contains.TryGetProperty("const", out var value) &&
               (section = value.GetString()) is not null;
    }

    private static bool TryReadRequiredProperty(
        JsonElement rule,
        string phase,
        out string? propertyName)
    {
        propertyName = null;
        if (!rule.TryGetProperty(phase, out var condition) ||
            !condition.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        propertyName = required.EnumerateArray().Select(value => value.GetString()).FirstOrDefault();
        return propertyName is not null;
    }

    private static JsonElement RequireSchemaAllowingType(JsonElement schema, string type, string label) =>
        RequireSchema(schema, candidate => AllowsType(candidate, type), label);

    private static JsonElement RequireSchemaWithProperty(JsonElement schema, string propertyName, string label) =>
        RequireSchema(
            schema,
            candidate => candidate.ValueKind == JsonValueKind.Object && candidate.TryGetProperty(propertyName, out _),
            label);

    private static JsonElement RequireSchema(
        JsonElement schema,
        Func<JsonElement, bool> predicate,
        string label)
    {
        if (TryFindSchema(schema, predicate, out var result))
        {
            return result;
        }

        throw new AssertionException($"Could not find the {label} schema in: {schema.GetRawText()}");
    }

    private static bool TryFindSchema(
        JsonElement schema,
        Func<JsonElement, bool> predicate,
        out JsonElement result)
    {
        if (predicate(schema))
        {
            result = schema;
            return true;
        }

        if (schema.ValueKind == JsonValueKind.Object)
        {
            foreach (var compositionKeyword in new[] { "anyOf", "oneOf", "allOf" })
            {
                if (!schema.TryGetProperty(compositionKeyword, out var variants) ||
                    variants.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var variant in variants.EnumerateArray())
                {
                    if (TryFindSchema(variant, predicate, out result))
                    {
                        return true;
                    }
                }
            }
        }

        result = default;
        return false;
    }

    private static bool AllowsType(JsonElement schema, string expectedType)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("type", out var type))
        {
            return false;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return string.Equals(type.GetString(), expectedType, StringComparison.Ordinal);
        }

        return type.ValueKind == JsonValueKind.Array &&
               type.EnumerateArray().Any(value =>
                   value.ValueKind == JsonValueKind.String &&
                   string.Equals(value.GetString(), expectedType, StringComparison.Ordinal));
    }

    private static JsonElement RequireObjectProperty(JsonElement value, string propertyName, string label)
    {
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Object)
        {
            return property;
        }

        throw new AssertionException($"Missing {label} in: {value.GetRawText()}");
    }

    private static int ReadInt32(JsonElement value, string propertyName)
    {
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(propertyName, out var property) &&
            property.TryGetInt32(out var result))
        {
            return result;
        }

        throw new AssertionException($"Missing integer '{propertyName}' in: {value.GetRawText()}");
    }

    private static bool ReadBoolean(JsonElement value, string propertyName)
    {
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(propertyName, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return property.GetBoolean();
        }

        throw new AssertionException($"Missing boolean '{propertyName}' in: {value.GetRawText()}");
    }

    private static bool ForbidsRequiredPropertiesTogether(
        JsonElement schema,
        string firstProperty,
        string secondProperty)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (schema.TryGetProperty("not", out var negated) &&
            negated.ValueKind == JsonValueKind.Object &&
            negated.TryGetProperty("required", out var required) &&
            required.ValueKind == JsonValueKind.Array)
        {
            var names = required
                .EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .ToHashSet(StringComparer.Ordinal);
            if (names.SetEquals([firstProperty, secondProperty]))
            {
                return true;
            }
        }

        foreach (var compositionKeyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (!schema.TryGetProperty(compositionKeyword, out var variants) ||
                variants.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            if (variants.EnumerateArray().Any(variant =>
                    ForbidsRequiredPropertiesTogether(variant, firstProperty, secondProperty)))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateExposedPropertyNames(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in schema.EnumerateArray())
            {
                foreach (var name in EnumerateExposedPropertyNames(item))
                {
                    yield return name;
                }
            }

            yield break;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in schema.EnumerateObject())
        {
            if (property.NameEquals("properties") && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var exposedProperty in property.Value.EnumerateObject())
                {
                    yield return exposedProperty.Name;
                }
            }

            foreach (var name in EnumerateExposedPropertyNames(property.Value))
            {
                yield return name;
            }
        }
    }
}
