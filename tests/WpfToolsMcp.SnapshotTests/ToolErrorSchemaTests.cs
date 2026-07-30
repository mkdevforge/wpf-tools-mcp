using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WpfToolsMcp.Contracts;

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

    [TestCase("core")]
    [TestCase("diagnostics")]
    public async Task Uia_locator_unmapped_result_validates_when_optional_sections_are_omitted(string profile)
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, profile);
        var tool = (await mcp.ListToolsAsync()).Single(item => item.Name == "get_uia_locators");
        var response = new GetUiaLocatorsResponse(
            Wpf: new WpfLocatorIdentity("Control", "Target", null, null, "/Window/Control", "wpf_1"),
            UiaMapping: new UiaMappingDiagnostics(
                Ambiguous: false,
                SelectedXPath: null,
                Candidates: [],
                ReturnedCandidates: 0,
                TotalCandidates: 0)
            {
                Status = ElementMappingStatus.Unmapped,
                Method = "scoredWindowScan",
                Score = 0,
                ScannedNodes = 1,
                ScanComplete = true,
                Evidence = ["no_relevant_candidates"]
            });
        var structuredContent = JsonSerializer.SerializeToElement(
            response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var schema = JsonSchema.FromText(GetOutputSchema(tool).GetRawText());
        var evaluation = schema.Evaluate(
            structuredContent,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        var ambiguousResponse = new GetUiaLocatorsResponse(
            Wpf: response.Wpf,
            UiaMapping: new UiaMappingDiagnostics(
                Ambiguous: true,
                SelectedXPath: null,
                Candidates:
                [
                    new UiaMappingCandidate(
                        ElementType: "Button",
                        AutomationId: "Target",
                        Name: "Target",
                        ClassName: "Button",
                        Bounds: new Rect(1, 2, 3, 4),
                        XPath: null,
                        Score: 200,
                        XPathOmitted: true)
                    {
                        Evidence =
                        [
                            "runtime_identity_available",
                            "uia_path_budget_exhausted",
                            "public_handle_not_registered"
                        ]
                    }
                ],
                ReturnedCandidates: 1,
                TotalCandidates: 1,
                Truncated: true)
            {
                Status = ElementMappingStatus.Ambiguous,
                Method = "scoredWindowScan",
                Score = 200,
                Evidence = ["scan_incomplete"],
                ScannedNodes = 1,
                ScanComplete = false,
                TruncatedReason = "maxNodes"
            });
        var ambiguousContent = JsonSerializer.SerializeToElement(
            ambiguousResponse,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var ambiguousEvaluation = schema.Evaluate(
            ambiguousContent,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        var withoutCandidates = JsonNode.Parse(structuredContent.GetRawText())!.AsObject();
        _ = withoutCandidates["uiaMapping"]!.AsObject().Remove("candidates");
        var withoutCandidatesEvaluation = schema.Evaluate(
            JsonSerializer.SerializeToElement(withoutCandidates),
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        var withoutCandidateScore = JsonNode.Parse(ambiguousContent.GetRawText())!.AsObject();
        _ = withoutCandidateScore["uiaMapping"]!["candidates"]!.AsArray()[0]!
            .AsObject()
            .Remove("score");
        var withoutCandidateScoreEvaluation = schema.Evaluate(
            JsonSerializer.SerializeToElement(withoutCandidateScore),
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.Multiple(() =>
        {
            Assert.That(structuredContent.TryGetProperty("uia", out _), Is.False);
            Assert.That(structuredContent.TryGetProperty("locatorSuggestions", out _), Is.False);
            Assert.That(structuredContent.TryGetProperty("flaUi", out _), Is.False);
            Assert.That(
                structuredContent.GetProperty("uiaMapping").TryGetProperty("selectedXPath", out _),
                Is.False);
            Assert.That(
                evaluation.IsValid,
                Is.True,
                $"get_uia_locators unmapped result did not match its output schema: {JsonSerializer.Serialize(evaluation)}");
            Assert.That(
                ambiguousEvaluation.IsValid,
                Is.True,
                $"get_uia_locators pathless ambiguity did not match its output schema: {JsonSerializer.Serialize(ambiguousEvaluation)}");
            Assert.That(withoutCandidatesEvaluation.IsValid, Is.False, "uiaMapping.candidates must remain required.");
            Assert.That(withoutCandidateScoreEvaluation.IsValid, Is.False, "candidate.score must remain required.");
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
