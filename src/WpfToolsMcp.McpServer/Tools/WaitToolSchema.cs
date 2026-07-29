using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

internal static class WaitToolSchema
{
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> CreateListToolsFilter() =>
        next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            foreach (var tool in result.Tools.Where(tool => tool.Name == "wait_for"))
            {
                Refine(tool);
            }

            return result;
        };

    private static void Refine(Tool tool)
    {
        if (JsonNode.Parse(tool.InputSchema.GetRawText()) is not JsonObject root ||
            root["properties"] is not JsonObject inputProperties ||
            inputProperties["condition"] is not JsonObject condition ||
            condition["anyOf"] is not JsonArray variants)
        {
            return;
        }

        foreach (var variantNode in variants)
        {
            if (variantNode is not JsonObject variant ||
                variant["properties"] is not JsonObject properties ||
                properties["kind"]?["const"]?.GetValue<string>() is not string kind)
            {
                continue;
            }

            switch (kind)
            {
                case nameof(WaitConditionKind.NumericValueEquals):
                    properties["comparison"] = CreateFixedStringSchema(nameof(WaitComparison.Equals));
                    properties["expected"] = CreateScalarSchema(WaitScalarKind.Number);
                    break;
                case nameof(WaitConditionKind.NameContains):
                    properties["comparison"] = CreateFixedStringSchema(nameof(WaitComparison.Contains));
                    properties["expected"] = CreateScalarSchema(WaitScalarKind.String);
                    break;
                case nameof(WaitConditionKind.DependencyPropertyValue):
                case nameof(WaitConditionKind.DataContextValue):
                    properties["expected"] = CreateScalarSchema(Enum.GetValues<WaitScalarKind>());
                    break;
                case nameof(WaitConditionKind.WindowOpen):
                case nameof(WaitConditionKind.WindowClosed):
                    if (properties["window"] is JsonObject window)
                    {
                        RefineWindowSelector(window);
                    }

                    break;
            }
        }

        tool.InputSchema = JsonSerializer.SerializeToElement(root);
    }

    private static JsonObject CreateFixedStringSchema(string value) =>
        new()
        {
            ["type"] = "string",
            ["const"] = value
        };

    private static JsonObject CreateScalarSchema(params WaitScalarKind[] kinds) =>
        new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray("kind"),
            ["anyOf"] = new JsonArray(kinds.Select(CreateScalarVariant).ToArray())
        };

    private static JsonObject CreateScalarVariant(WaitScalarKind kind)
    {
        var properties = new JsonObject
        {
            ["kind"] = new JsonObject { ["const"] = kind.ToString() }
        };
        var required = new JsonArray();

        switch (kind)
        {
            case WaitScalarKind.String:
                properties["stringValue"] = new JsonObject { ["type"] = "string" };
                required.Add("stringValue");
                break;
            case WaitScalarKind.Number:
                properties["numberValue"] = new JsonObject { ["type"] = "number" };
                required.Add("numberValue");
                break;
            case WaitScalarKind.Boolean:
                properties["booleanValue"] = new JsonObject { ["type"] = "boolean" };
                required.Add("booleanValue");
                break;
            case WaitScalarKind.Null:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        return new JsonObject
        {
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        };
    }

    private static void RefineWindowSelector(JsonObject window)
    {
        if (window["properties"] is not JsonObject properties)
        {
            return;
        }

        properties["handle"] = PositiveIntegerSchema();
        properties["ownerHandle"] = PositiveIntegerSchema();
        properties["title"] = NonWhitespaceStringSchema();
        properties["titleContains"] = NonWhitespaceStringSchema();
        properties["frameworkId"] = NonWhitespaceStringSchema();
        window["additionalProperties"] = false;
        window["anyOf"] = new JsonArray(
            RequiredProperty("handle"),
            RequiredProperty("title"),
            RequiredProperty("titleContains"),
            RequiredProperty("ownerHandle"),
            RequiredProperty("frameworkId"));
    }

    private static JsonObject RequiredProperty(string propertyName) =>
        new() { ["required"] = new JsonArray(propertyName) };

    private static JsonObject PositiveIntegerSchema() =>
        new()
        {
            ["type"] = "integer",
            ["minimum"] = 1
        };

    private static JsonObject NonWhitespaceStringSchema() =>
        new()
        {
            ["type"] = "string",
            ["minLength"] = 1,
            ["pattern"] = "\\S"
        };
}
