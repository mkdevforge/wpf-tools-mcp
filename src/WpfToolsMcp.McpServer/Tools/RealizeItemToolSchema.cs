using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

internal static class RealizeItemToolSchema
{
    internal const int MinIndex = 0;

    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> CreateListToolsFilter() =>
        next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            foreach (var tool in result.Tools.Where(tool => tool.Name == "realize_item"))
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
        JsonArray constraints;
        if (root["allOf"] is JsonArray existingConstraints)
        {
            constraints = existingConstraints;
        }
        else
        {
            constraints = [];
            root["allOf"] = constraints;
        }

        constraints.Add(ExactlyOne(
            ("containerLocator", new JsonObject { ["type"] = "object" }),
            ("containerElementId", NonWhitespaceStringSchema())));
        constraints.Add(ExactlyOne(
            ("index", IntegerSchema(MinIndex)),
            ("name", ExactNameSchema())));

        RefineInteger(properties["index"], MinIndex, maximum: null);
        RefineInteger(
            properties["maxProviderCalls"],
            RealizeItemLimits.MinimumProviderCalls,
            RealizeItemLimits.MaximumProviderCalls);
        RefineInteger(
            properties["advisoryElapsedLimitMs"],
            RealizeItemLimits.MinimumAdvisoryElapsedLimitMs,
            RealizeItemLimits.MaximumAdvisoryElapsedLimitMs);
        RefineInteger(
            properties["pollIntervalMs"],
            RealizeItemLimits.MinimumPollIntervalMs,
            RealizeItemLimits.MaximumPollIntervalMs);

        tool.InputSchema = JsonSerializer.SerializeToElement(root);
    }

    private static JsonObject ExactlyOne(
        (string Name, JsonObject Schema) first,
        (string Name, JsonObject Schema) second) =>
        new()
        {
            ["oneOf"] = new JsonArray(
                RequiredWithout(first.Name, first.Schema, second.Name),
                RequiredWithout(second.Name, second.Schema, first.Name))
        };

    private static JsonObject RequiredWithout(
        string requiredProperty,
        JsonObject requiredSchema,
        string forbiddenProperty) =>
        new()
        {
            ["required"] = new JsonArray(requiredProperty),
            ["properties"] = new JsonObject
            {
                [requiredProperty] = requiredSchema
            },
            ["not"] = new JsonObject
            {
                ["required"] = new JsonArray(forbiddenProperty)
            }
        };

    private static JsonObject IntegerSchema(int minimum) =>
        new()
        {
            ["type"] = "integer",
            ["minimum"] = minimum
        };

    private static JsonObject NonWhitespaceStringSchema() =>
        new()
        {
            ["type"] = "string",
            ["minLength"] = 1,
            ["pattern"] = "\\S"
        };

    private static JsonObject ExactNameSchema() =>
        new()
        {
            ["type"] = "string",
            ["minLength"] = 1
        };

    private static void RefineInteger(JsonNode? node, int minimum, int? maximum)
    {
        if (FindSchema(node, "type", expectedValue: "integer") is not JsonObject integer)
        {
            return;
        }

        integer["minimum"] = minimum;
        if (maximum is int maximumValue)
        {
            integer["maximum"] = maximumValue;
        }
        else
        {
            integer.Remove("maximum");
        }
    }

    private static JsonObject? FindSchema(JsonNode? node, string requiredProperty, string expectedValue)
    {
        if (node is not JsonObject value)
        {
            return null;
        }

        if (HasExpectedValue(value[requiredProperty], expectedValue))
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
