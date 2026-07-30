using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WpfToolsMcp.McpServer.Tools;

internal static class McpToolOutputSchema
{
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> CreateListToolsFilter() =>
        next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            foreach (var tool in result.Tools)
            {
                Compose(tool);
            }

            return result;
        };

    internal static void Compose(Tool tool)
    {
        if (tool.OutputSchema is not { } outputSchema ||
            JsonNode.Parse(outputSchema.GetRawText()) is not JsonObject root ||
            HasErrorBranch(root))
        {
            return;
        }

        var success = (JsonObject)root.DeepClone();
        RebaseLocalReferences(success);
        var composed = new JsonObject
        {
            ["type"] = "object",
            ["oneOf"] = new JsonArray(success, CreateErrorSchema())
        };
        tool.OutputSchema = JsonSerializer.SerializeToElement(composed);
    }

    private static bool HasErrorBranch(JsonObject root)
    {
        if (root["oneOf"] is not JsonArray branches)
        {
            return false;
        }

        return branches.Any(branch =>
            branch is JsonObject candidate &&
            candidate["required"] is JsonArray required &&
            required.Any(item => item?.GetValue<string>() == "error") &&
            candidate["properties"] is JsonObject properties &&
            properties["error"] is JsonObject);
    }

    private static void RebaseLocalReferences(JsonNode node)
    {
        if (node is JsonObject value)
        {
            foreach (var property in value.ToArray())
            {
                if ((property.Key == "$ref" || property.Key == "$dynamicRef") &&
                    property.Value is JsonValue reference &&
                    reference.TryGetValue<string>(out var pointer) &&
                    pointer.StartsWith("#/", StringComparison.Ordinal))
                {
                    value[property.Key] = "#/oneOf/0/" + pointer[2..];
                    continue;
                }

                if (property.Value is not null)
                {
                    RebaseLocalReferences(property.Value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    RebaseLocalReferences(item);
                }
            }
        }
    }

    private static JsonObject CreateErrorSchema() =>
        new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray("error"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("code", "detail"),
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject
                    {
                        ["code"] = TokenSchema(),
                        ["detail"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["minLength"] = 1,
                            ["maxLength"] = 512
                        },
                        ["stage"] = TokenSchema(),
                        ["retryable"] = new JsonObject { ["type"] = "boolean" },
                        ["retryAfterMs"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["minimum"] = 1
                        },
                        ["recoveryActions"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["maxItems"] = 8,
                            ["uniqueItems"] = true,
                            ["items"] = TokenSchema()
                        },
                        ["context"] = CreateContextSchema()
                    }
                }
            }
        };

    private static JsonObject CreateContextSchema() =>
        new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["sessionId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^[0-9a-f]{32}$",
                    ["maxLength"] = 128
                },
                ["windowHandle"] = PositiveIntegerSchema(),
                ["elementId"] = ElementIdSchema(),
                ["backend"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("Auto", "Uia", "Wpf")
                },
                ["returnedCandidates"] = NonNegativeIntegerSchema(maximum: 25),
                ["discoveredCandidates"] = NonNegativeIntegerSchema(),
                ["truncated"] = new JsonObject { ["type"] = "boolean" },
                ["candidates"] = new JsonObject
                {
                    ["type"] = "array",
                    ["maxItems"] = 25,
                    ["items"] = CreateCandidateSchema()
                }
            }
        };

    private static JsonObject CreateCandidateSchema() =>
        new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray("kind", "index"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("Process", "Element")
                },
                ["index"] = NonNegativeIntegerSchema(),
                ["processInstanceId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["maxLength"] = 128,
                    ["pattern"] = "^[1-9][0-9]*:[1-9][0-9]*$"
                },
                ["pid"] = PositiveIntegerSchema(),
                ["windowHandle"] = PositiveIntegerSchema(),
                ["elementId"] = ElementIdSchema()
            }
        };

    private static JsonObject TokenSchema() =>
        new()
        {
            ["type"] = "string",
            ["minLength"] = 1,
            ["maxLength"] = 64,
            ["pattern"] = "^[a-z][a-z0-9_]*$"
        };

    private static JsonObject ElementIdSchema() =>
        new()
        {
            ["type"] = "string",
            ["maxLength"] = 128,
            ["pattern"] = "^(uia|wpf)_[A-Za-z0-9_-]{16}$"
        };

    private static JsonObject PositiveIntegerSchema() =>
        new()
        {
            ["type"] = "integer",
            ["minimum"] = 1
        };

    private static JsonObject NonNegativeIntegerSchema(int? maximum = null)
    {
        var schema = new JsonObject
        {
            ["type"] = "integer",
            ["minimum"] = 0
        };
        if (maximum is not null)
        {
            schema["maximum"] = maximum.Value;
        }

        return schema;
    }
}
