using System.Text.Json;
using Json.Schema;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
public sealed class ToolErrorSchemaTests
{
    [TestCase("core")]
    [TestCase("diagnostics")]
    public async Task Profiles_advertise_idempotent_success_or_error_schemas(string profile)
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, profile);

        var first = await mcp.ListToolsAsync();
        var second = await mcp.ListToolsAsync();
        var secondByName = second.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        Assert.That(secondByName.Keys, Is.EquivalentTo(first.Select(tool => tool.Name)));
        foreach (var tool in first)
        {
            var schema = GetOutputSchema(tool);
            Assert.That(schema.GetRawText(), Is.EqualTo(GetOutputSchema(secondByName[tool.Name]).GetRawText()), tool.Name);
            Assert.That(schema.GetProperty("type").GetString(), Is.EqualTo("object"), tool.Name);

            var branches = schema.GetProperty("oneOf").EnumerateArray().ToArray();
            Assert.That(branches, Has.Length.EqualTo(2), tool.Name);
            Assert.That(IsErrorBranch(branches[0]), Is.False, tool.Name);
            Assert.That(IsErrorBranch(branches[1]), Is.True, tool.Name);
            Assert.That(
                branches.Any(branch => branch.TryGetProperty("oneOf", out var nested) &&
                                       nested.EnumerateArray().Any(IsErrorBranch)),
                Is.False,
                $"{tool.Name} was wrapped more than once.");

            foreach (var reference in EnumerateLocalReferences(schema))
            {
                Assert.That(
                    TryResolvePointer(schema, reference, out _),
                    Is.True,
                    $"{tool.Name} contains an unresolved local reference '{reference}'.");
            }
        }
    }

    [Test]
    public async Task Representative_success_and_error_results_validate_against_output_schemas()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var core = await McpTestContext.StartAsync(serverExe, "core");
        await using var diagnostics = await McpTestContext.StartAsync(serverExe, "diagnostics");
        var coreTools = (await core.ListToolsAsync()).ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var diagnosticTools = (await diagnostics.ListToolsAsync()).ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        var sessions = await core.CallToolResultAsync("list_sessions");
        var missingProcess = await core.CallToolResultAsync("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe")
        });
        var invalidSubscription = await diagnostics.CallToolResultAsync(
            "subscribe_property_changes",
            new Dictionary<string, object?>());

        AssertValid(coreTools["list_sessions"], sessions);
        AssertValid(coreTools["launch_app"], missingProcess);
        AssertValid(diagnosticTools["subscribe_property_changes"], invalidSubscription);
        Assert.Multiple(() =>
        {
            Assert.That(sessions.IsError, Is.Not.True);
            Assert.That(missingProcess.IsError, Is.True);
            Assert.That(invalidSubscription.IsError, Is.True);
        });
    }

    [Test]
    public async Task Special_success_validates_against_the_success_branch()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        var appExe = TestAppPaths.FindTestAppExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, "core");
        var launchTool = (await mcp.ListToolsAsync()).Single(tool => tool.Name == "launch_app");
        string? sessionId = null;

        try
        {
            var result = await mcp.CallToolResultAsync("launch_app", new Dictionary<string, object?>
            {
                ["exePath"] = appExe,
                ["workingDirectory"] = Path.GetDirectoryName(appExe)!
            });
            sessionId = result.StructuredContent!.Value.GetProperty("sessionId").GetString();

            Assert.That(result.IsError, Is.Not.True);
            AssertValid(launchTool, result);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                try
                {
                    _ = await mcp.CallToolResultAsync("close_session", new Dictionary<string, object?>
                    {
                        ["sessionId"] = sessionId,
                        ["force"] = true
                    });
                }
                catch
                {
                }
            }
        }
    }

    private static void AssertValid(McpClientTool tool, CallToolResult result)
    {
        Assert.That(result.StructuredContent, Is.Not.Null, tool.Name);
        var schema = JsonSchema.FromText(GetOutputSchema(tool).GetRawText());
        var evaluation = schema.Evaluate(
            result.StructuredContent!.Value,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.That(
            evaluation.IsValid,
            Is.True,
            $"{tool.Name} result did not match its output schema: {JsonSerializer.Serialize(evaluation)}");
    }

    private static JsonElement GetOutputSchema(McpClientTool tool) =>
        tool.ProtocolTool.OutputSchema ?? throw new AssertionException($"{tool.Name} has no output schema.");

    private static bool IsErrorBranch(JsonElement branch)
    {
        if (!branch.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return required.EnumerateArray().Any(item => item.GetString() == "error");
    }

    private static IEnumerable<string> EnumerateLocalReferences(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if ((property.Name == "$ref" || property.Name == "$dynamicRef") &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is { } reference &&
                    reference.StartsWith("#/", StringComparison.Ordinal))
                {
                    yield return reference;
                }

                foreach (var nested in EnumerateLocalReferences(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                foreach (var nested in EnumerateLocalReferences(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool TryResolvePointer(JsonElement root, string reference, out JsonElement resolved)
    {
        resolved = root;
        foreach (var encodedSegment in reference[2..].Split('/'))
        {
            var segment = Uri.UnescapeDataString(encodedSegment)
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (resolved.ValueKind == JsonValueKind.Object)
            {
                if (!resolved.TryGetProperty(segment, out resolved))
                {
                    return false;
                }
            }
            else if (resolved.ValueKind == JsonValueKind.Array &&
                     int.TryParse(segment, out var index) &&
                     index >= 0 && index < resolved.GetArrayLength())
            {
                resolved = resolved[index];
            }
            else
            {
                return false;
            }
        }

        return true;
    }
}
