using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

internal static class DiagnosticSnapshotToolSchema
{
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> CreateListToolsFilter() =>
        next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            foreach (var tool in result.Tools.Where(tool => tool.Name == "capture_diagnostic_snapshot"))
            {
                Refine(tool);
            }

            return result;
        };

    private static void Refine(Tool tool)
    {
        if (JsonNode.Parse(tool.InputSchema.GetRawText()) is not JsonObject root ||
            root["properties"] is not JsonObject properties)
        {
            return;
        }

        root["additionalProperties"] = false;
        root["allOf"] = new JsonArray(
            new JsonObject
            {
                ["not"] = new JsonObject
                {
                    ["required"] = new JsonArray("locator", "elementId")
                }
            },
            RequirePropertyForSection(DiagnosticSection.WpfProperties, "propertyNames"),
            RequireSectionForProperty("propertyNames", DiagnosticSection.WpfProperties),
            RequireSectionForProperty("dataContextProperties", DiagnosticSection.DataContext));

        if (FindSchema(properties["sections"], "items") is JsonObject sections)
        {
            sections["minItems"] = 1;
            sections["maxItems"] = DiagnosticSnapshotLimits.MaxSections;
            sections["uniqueItems"] = true;
        }

        RefineOptionalString(properties["elementId"]);
        RefineNameArray(properties["propertyNames"]);
        RefineNameArray(properties["dataContextProperties"]);
        RefineInteger(properties["timeoutMs"], DiagnosticSnapshotLimits.MinTimeoutMs, DiagnosticSnapshotLimits.MaxTimeoutMs);

        if (FindSchema(properties["budget"], "properties") is JsonObject budget &&
            budget["properties"] is JsonObject budgetProperties)
        {
            budget["additionalProperties"] = false;
            RefineInteger(budgetProperties["maxDepth"], DiagnosticSnapshotLimits.MinDepth, DiagnosticSnapshotLimits.MaxDepth);
            RefineInteger(budgetProperties["maxItems"], DiagnosticSnapshotLimits.MinItems, DiagnosticSnapshotLimits.MaxItems);
            RefineInteger(budgetProperties["maxNodes"], DiagnosticSnapshotLimits.MinNodes, DiagnosticSnapshotLimits.MaxNodes);
            RefineInteger(budgetProperties["maxValueLength"], DiagnosticSnapshotLimits.MinValueLength, DiagnosticSnapshotLimits.MaxValueLength);
            RefineInteger(budgetProperties["maxPayloadChars"], DiagnosticSnapshotLimits.MinPayloadChars, DiagnosticSnapshotLimits.MaxPayloadChars);
        }

        tool.InputSchema = JsonSerializer.SerializeToElement(root);
    }

    private static void RefineNameArray(JsonNode? node)
    {
        if (FindSchema(node, "items") is not JsonObject array)
        {
            return;
        }

        array["minItems"] = 1;
        array["maxItems"] = DiagnosticSnapshotLimits.MaxPropertyNames;
        array["uniqueItems"] = true;
        if (array["items"] is JsonObject items)
        {
            items["minLength"] = 1;
            items["maxLength"] = DiagnosticSnapshotLimits.MaxPropertyNameLength;
            items["pattern"] = "\\S";
        }
    }

    private static void RefineOptionalString(JsonNode? node)
    {
        if (FindSchema(node, "type", expectedValue: "string") is JsonObject value)
        {
            value["minLength"] = 1;
            value["pattern"] = "\\S";
        }
    }

    private static JsonObject RequirePropertyForSection(
        DiagnosticSection section,
        string propertyName) =>
        new()
        {
            ["if"] = new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["sections"] = ContainsSection(section)
                },
                ["required"] = new JsonArray("sections")
            },
            ["then"] = new JsonObject
            {
                ["required"] = new JsonArray(propertyName),
                ["properties"] = new JsonObject
                {
                    [propertyName] = new JsonObject { ["type"] = "array" }
                }
            }
        };

    private static JsonObject RequireSectionForProperty(
        string propertyName,
        DiagnosticSection section) =>
        new()
        {
            ["if"] = new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    [propertyName] = new JsonObject { ["type"] = "array" }
                },
                ["required"] = new JsonArray(propertyName)
            },
            ["then"] = new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["sections"] = ContainsSection(section)
                }
            }
        };

    private static JsonObject ContainsSection(DiagnosticSection section) =>
        new()
        {
            ["contains"] = new JsonObject
            {
                ["const"] = section.ToString()
            }
        };

    private static void RefineInteger(JsonNode? node, int minimum, int maximum)
    {
        if (FindSchema(node, "type", expectedValue: "integer") is not JsonObject value)
        {
            return;
        }

        value["minimum"] = minimum;
        value["maximum"] = maximum;
    }

    private static JsonObject? FindSchema(JsonNode? node, string requiredProperty, string? expectedValue = null)
    {
        if (node is not JsonObject value)
        {
            return null;
        }

        if (value[requiredProperty] is not null &&
            (expectedValue is null || HasExpectedValue(value[requiredProperty], expectedValue)))
        {
            return value;
        }

        if (value["anyOf"] is JsonArray variants)
        {
            foreach (var variant in variants)
            {
                var match = FindSchema(variant, requiredProperty, expectedValue);
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return null;
    }

    private static bool HasExpectedValue(JsonNode? node, string expectedValue)
    {
        if (node is JsonValue scalar && scalar.TryGetValue<string>(out var value))
        {
            return string.Equals(value, expectedValue, StringComparison.Ordinal);
        }

        return node is JsonArray values &&
               values.Any(item => item is JsonValue candidate &&
                                  candidate.TryGetValue<string>(out var value) &&
                                  string.Equals(value, expectedValue, StringComparison.Ordinal));
    }
}
