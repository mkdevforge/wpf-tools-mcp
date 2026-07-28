using System.Text.Json.Nodes;
using System.Threading;
using NUnit.Framework;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class ElementPropertiesTests
{
    private static readonly string[] ExpectedSummaryPropertyNames =
    [
        "AcceleratorKey",
        "AccessKey",
        "AriaProperties",
        "AriaRole",
        "ClickablePoint",
        "FrameworkId",
        "FullDescription",
        "HasKeyboardFocus",
        "HelpText",
        "IsContentElement",
        "IsControlElement",
        "IsKeyboardFocusable",
        "IsPassword",
        "IsRequiredForForm",
        "ItemStatus",
        "ItemType",
        "LabeledBy",
        "LocalizedControlType",
        "Orientation",
        "ProcessId"
    ];

    [Test]
    public async Task GetElementProperties_profile_schemas_keep_controls_diagnostics_only()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var core = await McpTestContext.StartAsync(
            serverExe,
            toolProfile: null,
            environmentVariables: new Dictionary<string, string?> { ["WPF_TOOLS_MCP_TOOL_PROFILE"] = null });
        await using var diagnostics = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");

        var coreSchema = (await core.ListToolsAsync()).Single(tool => tool.Name == "get_element_properties").JsonSchema;
        var diagnosticsSchema = (await diagnostics.ListToolsAsync()).Single(tool => tool.Name == "get_element_properties").JsonSchema;
        var coreProperties = coreSchema.GetProperty("properties");
        var diagnosticsProperties = diagnosticsSchema.GetProperty("properties");

        Assert.Multiple(() =>
        {
            Assert.That(coreProperties.TryGetProperty("preset", out _), Is.False);
            Assert.That(coreProperties.TryGetProperty("maxProperties", out _), Is.False);
            Assert.That(diagnosticsProperties.TryGetProperty("preset", out _), Is.True);
            Assert.That(diagnosticsProperties.TryGetProperty("maxProperties", out _), Is.True);
        });
    }

    [TestCase(null)]
    [TestCase("diagnostics")]
    public async Task GetElementProperties_defaults_to_bounded_summary(string? toolProfile)
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(
            serverExe,
            toolProfile,
            environmentVariables: new Dictionary<string, string?> { ["WPF_TOOLS_MCP_TOOL_PROFILE"] = null });

        var launch = await LaunchTestAppAsync(mcp);
        try
        {
            var response = await GetTextBoxPropertiesAsync(mcp, launch.SessionId);

            Assert.Multiple(() =>
            {
                Assert.That(response.Preset, Is.EqualTo(ElementPropertiesPreset.Summary));
                Assert.That(response.Properties.Keys, Is.EqualTo(ExpectedSummaryPropertyNames));
                Assert.That(response.ReturnedProperties, Is.EqualTo(response.Properties.Count));
                Assert.That(response.SelectedProperties, Is.EqualTo(response.ReturnedProperties));
                Assert.That(response.ScannedProperties, Is.GreaterThan(response.SelectedProperties));
                Assert.That(response.Truncated, Is.False);
                Assert.That(response.TruncatedReason, Is.Null);
            });

            AssertValuePattern(response);
        }
        finally
        {
            await CloseSessionBestEffortAsync(mcp, launch.SessionId);
        }
    }

    [Test]
    public async Task GetElementProperties_full_preset_honors_limit_and_reports_truncation()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");

        var launch = await LaunchTestAppAsync(mcp);
        try
        {
            var truncated = await GetTextBoxPropertiesAsync(
                mcp,
                launch.SessionId,
                new Dictionary<string, object?>
                {
                    ["preset"] = "full",
                    ["maxProperties"] = 3
                });

            Assert.Multiple(() =>
            {
                Assert.That(truncated.Preset, Is.EqualTo(ElementPropertiesPreset.Full));
                Assert.That(truncated.Properties.Keys, Is.EqualTo(new[] { "AcceleratorKey", "AccessKey", "AnnotationObjects" }));
                Assert.That(truncated.ReturnedProperties, Is.EqualTo(3));
                Assert.That(truncated.SelectedProperties, Is.EqualTo(truncated.ScannedProperties));
                Assert.That(truncated.SelectedProperties, Is.GreaterThan(truncated.ReturnedProperties));
                Assert.That(truncated.Truncated, Is.True);
                Assert.That(truncated.TruncatedReason, Is.EqualTo("maxProperties"));
                Assert.That(truncated.TruncatedReasons, Is.EqualTo(new[] { "maxProperties" }));
            });

            AssertValuePattern(truncated);

            var complete = await GetTextBoxPropertiesAsync(
                mcp,
                launch.SessionId,
                new Dictionary<string, object?>
                {
                    ["preset"] = "full",
                    ["maxProperties"] = 200
                });

            Assert.Multiple(() =>
            {
                Assert.That(complete.ReturnedProperties, Is.EqualTo(complete.Properties.Count));
                Assert.That(complete.ReturnedProperties, Is.EqualTo(complete.SelectedProperties));
                Assert.That(complete.SelectedProperties, Is.EqualTo(complete.ScannedProperties));
                Assert.That(complete.Truncated, Is.False);
                Assert.That(complete.TruncatedReason, Is.Null);
            });
        }
        finally
        {
            await CloseSessionBestEffortAsync(mcp, launch.SessionId);
        }
    }

    [Test]
    public async Task GetElementProperties_caps_value_pattern_text_and_reports_value_truncation()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");

        var launch = await LaunchTestAppAsync(mcp);
        try
        {
            var longValue = new string('x', PropertyValueBudget.MaxStringLength + 137);
            var set = await mcp.CallToolAsync<SetValueResponse>("set_value", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_TextBox" },
                ["text"] = longValue
            });
            Assert.That(set.Set, Is.True);

            var response = await GetTextBoxPropertiesAsync(mcp, launch.SessionId);
            var valuePattern = response.Patterns["Value"]?["values"]?["Value"]?.GetValue<string>();

            Assert.Multiple(() =>
            {
                Assert.That(valuePattern, Has.Length.EqualTo(PropertyValueBudget.MaxStringLength));
                Assert.That(valuePattern, Is.EqualTo(longValue[..PropertyValueBudget.MaxStringLength]));
                Assert.That(response.Truncated, Is.True);
                Assert.That(response.TruncatedReason, Is.EqualTo("maxStringLength"));
                Assert.That(response.TruncatedReasons, Is.EqualTo(new[] { "maxStringLength" }));
            });
        }
        finally
        {
            await CloseSessionBestEffortAsync(mcp, launch.SessionId);
        }
    }

    [Test]
    public void Bounded_property_value_serializer_caps_collections_and_depth()
    {
        var collectionBudget = new PropertyValueBudget();
        var collection = BoundedPropertyValueSerializer.Serialize(
            Enumerable.Range(0, PropertyValueBudget.MaxCollectionItems + 1),
            collectionBudget) as JsonArray;

        var depthBudget = new PropertyValueBudget();
        var nested = BoundedPropertyValueSerializer.Serialize(
            new object[] { new object[] { new object[] { "too deep" } } },
            depthBudget) as JsonArray;
        var depthSentinel = nested?[0]?[0]?.GetValue<string>();

        Assert.Multiple(() =>
        {
            Assert.That(collection, Has.Count.EqualTo(PropertyValueBudget.MaxCollectionItems));
            Assert.That(collectionBudget.Truncation.HasFlag(PropertyValueTruncation.CollectionItems), Is.True);
            Assert.That(
                BoundedPropertyValueSerializer.GetTruncatedReason(false, collectionBudget),
                Is.EqualTo("maxCollectionItems"));
            Assert.That(depthSentinel, Is.EqualTo("<truncated:maxValueDepth>"));
            Assert.That(depthBudget.Truncation.HasFlag(PropertyValueTruncation.ValueDepth), Is.True);
            Assert.That(
                BoundedPropertyValueSerializer.GetTruncatedReason(false, depthBudget),
                Is.EqualTo("maxValueDepth"));
        });
    }

    [Test]
    public void Bounded_property_value_serializer_uses_stable_reason_precedence()
    {
        var budget = new PropertyValueBudget();
        _ = BoundedPropertyValueSerializer.Serialize(
            Enumerable.Range(0, PropertyValueBudget.MaxCollectionItems + 1),
            budget);
        _ = BoundedPropertyValueSerializer.Serialize(
            new object[] { new object[] { new object[] { "too deep" } } },
            budget);
        _ = BoundedPropertyValueSerializer.Serialize(
            Enumerable.Repeat(
                new string('x', PropertyValueBudget.MaxStringLength + 1),
                PropertyValueBudget.MaxCollectionItems + 1),
            budget);

        Assert.Multiple(() =>
        {
            Assert.That(budget.Truncation.HasFlag(PropertyValueTruncation.StringLength), Is.True);
            Assert.That(budget.Truncation.HasFlag(PropertyValueTruncation.CollectionItems), Is.True);
            Assert.That(budget.Truncation.HasFlag(PropertyValueTruncation.ValueDepth), Is.True);
            Assert.That(budget.Truncation.HasFlag(PropertyValueTruncation.ValueCharacters), Is.True);
            Assert.That(
                BoundedPropertyValueSerializer.GetTruncatedReason(false, budget),
                Is.EqualTo("maxValueCharacters"));
            Assert.That(
                BoundedPropertyValueSerializer.GetTruncatedReason(true, budget),
                Is.EqualTo("maxProperties"));
            Assert.That(
                BoundedPropertyValueSerializer.GetTruncatedReasons(
                    propertiesTruncated: true,
                    budget: budget,
                    mappingCandidatesTruncated: true),
                Is.EqualTo(new[]
                {
                    "maxProperties",
                    "maxMappingCandidates",
                    "maxValueCharacters",
                    "maxStringLength",
                    "maxCollectionItems",
                    "maxValueDepth"
                }));
        });
    }

    [Test]
    public void Bounded_property_value_serializer_enforces_shared_character_budget()
    {
        var budget = new PropertyValueBudget();
        var serialized = BoundedPropertyValueSerializer.Serialize(
            Enumerable.Repeat(
                new string('x', PropertyValueBudget.MaxStringLength),
                PropertyValueBudget.MaxCollectionItems),
            budget);

        Assert.Multiple(() =>
        {
            Assert.That(serialized, Is.InstanceOf<JsonArray>());
            Assert.That(serialized!.ToJsonString(), Has.Length.LessThanOrEqualTo(
                PropertyValueBudget.MaxSerializedValueCharacters));
            Assert.That(budget.Truncation.HasFlag(PropertyValueTruncation.ValueCharacters), Is.True);
            Assert.That(
                BoundedPropertyValueSerializer.GetTruncatedReason(false, budget),
                Is.EqualTo("maxValueCharacters"));
        });
    }

    [Test]
    public void Bounded_property_value_serializer_distinguishes_null_from_omitted_values()
    {
        var budget = new PropertyValueBudget();

        var includedNull = BoundedPropertyValueSerializer.TrySerialize(
            value: null,
            budget,
            out var nullValue);
        _ = BoundedPropertyValueSerializer.Serialize(
            Enumerable.Repeat(
                new string('x', PropertyValueBudget.MaxStringLength),
                PropertyValueBudget.MaxCollectionItems),
            budget);
        var includedAfterExhaustion = BoundedPropertyValueSerializer.TrySerialize(
            "omitted",
            budget,
            out var omittedValue);

        Assert.Multiple(() =>
        {
            Assert.That(includedNull, Is.True);
            Assert.That(nullValue, Is.Null);
            Assert.That(includedAfterExhaustion, Is.False);
            Assert.That(omittedValue, Is.Null);
            Assert.That(budget.Truncation.HasFlag(PropertyValueTruncation.ValueCharacters), Is.True);
        });
    }

    [Test]
    public void Oversized_xpaths_are_omitted_instead_of_returned_as_invalid_prefixes()
    {
        var budget = new PropertyValueBudget();
        var oversizedXPath = "/Window/" + new string('x', PropertyValueBudget.MaxXPathLength);

        var serialized = BoundedPropertyValueSerializer.SerializeXPath(
            oversizedXPath,
            budget,
            out var omitted);

        Assert.Multiple(() =>
        {
            Assert.That(serialized, Is.Null);
            Assert.That(omitted, Is.True);
            Assert.That(budget.Truncation.HasFlag(PropertyValueTruncation.XPathLength), Is.True);
            Assert.That(
                BoundedPropertyValueSerializer.GetTruncatedReasons(false, budget),
                Is.EqualTo(new[] { "maxXPathLength" }));
        });
    }

    [Test]
    public void Uia_mapping_candidates_are_bounded_with_explicit_counts()
    {
        var source = Enumerable.Range(1, AutomationController.MaximumUiaMappingCandidates + 2)
            .Select(index => new UiaMappingCandidate(
                ElementType: "Button",
                AutomationId: $"Button_{index}",
                Name: $"Button {index}",
                ClassName: "Button",
                Bounds: new Rect(0, 0, 100, 30),
                XPath: $"/Window/Button[{index}]",
                Score: 100))
            .ToArray();
        var mapping = new UiaMappingDiagnostics(
            Ambiguous: true,
            SelectedXPath: source[0].XPath,
            Candidates: source);
        var budget = new PropertyValueBudget();

        var bounded = AutomationController.BoundUiaMappingDiagnostics(
            mapping,
            budget,
            out var candidatesLimitReached);

        Assert.Multiple(() =>
        {
            Assert.That(candidatesLimitReached, Is.True);
            Assert.That(bounded, Is.Not.Null);
            Assert.That(bounded!.ReturnedCandidates, Is.EqualTo(AutomationController.MaximumUiaMappingCandidates));
            Assert.That(bounded.TotalCandidates, Is.EqualTo(source.Length));
            Assert.That(bounded.Truncated, Is.True);
            Assert.That(bounded.Candidates, Has.Count.EqualTo(AutomationController.MaximumUiaMappingCandidates));
            Assert.That(bounded.SelectedXPath, Is.EqualTo(mapping.SelectedXPath));
            Assert.That(bounded.SelectedXPathOmitted, Is.Not.True);
            Assert.That(bounded.Candidates[^1].XPath, Is.EqualTo(source[AutomationController.MaximumUiaMappingCandidates - 1].XPath));
            Assert.That(
                BoundedPropertyValueSerializer.GetTruncatedReasons(false, budget, candidatesLimitReached),
                Is.EqualTo(new[] { "maxMappingCandidates" }));
        });
    }

    [Test]
    public void Bounded_property_value_serializer_reduces_unknown_objects_to_capped_strings()
    {
        var budget = new PropertyValueBudget();
        var serialized = BoundedPropertyValueSerializer.Serialize(
            new OversizedStringValue(),
            budget)?.GetValue<string>();

        Assert.Multiple(() =>
        {
            Assert.That(serialized, Has.Length.EqualTo(PropertyValueBudget.MaxStringLength));
            Assert.That(budget.Truncation.HasFlag(PropertyValueTruncation.StringLength), Is.True);
            Assert.That(
                BoundedPropertyValueSerializer.GetTruncatedReason(false, budget),
                Is.EqualTo("maxStringLength"));
        });
    }

    private static async Task<LaunchAppResponse> LaunchTestAppAsync(McpTestContext mcp)
    {
        var exePath = TestAppPaths.FindTestAppExecutable();
        return await mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = Path.GetDirectoryName(exePath)!
        });
    }

    private static async Task<GetElementPropertiesResponse> GetTextBoxPropertiesAsync(
        McpTestContext mcp,
        string sessionId,
        IReadOnlyDictionary<string, object?>? options = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_TextBox" }
        };

        if (options is not null)
        {
            foreach (var option in options)
            {
                arguments[option.Key] = option.Value;
            }
        }

        return await mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", arguments);
    }

    private static void AssertValuePattern(GetElementPropertiesResponse response)
    {
        Assert.That(response.Patterns.TryGetValue("Value", out var valuePattern), Is.True);
        var values = valuePattern?["values"] as JsonObject;

        Assert.Multiple(() =>
        {
            Assert.That(values?["Value"]?.GetValue<string>(), Is.EqualTo("Hello WPF Tools MCP"));
            Assert.That(values?["IsReadOnly"]?.GetValue<bool>(), Is.False);
        });
    }

    private static async Task CloseSessionBestEffortAsync(McpTestContext mcp, string sessionId)
    {
        try
        {
            _ = await mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["force"] = true,
                ["timeoutMs"] = 2000
            });
        }
        catch
        {
        }
    }

    private sealed class OversizedStringValue
    {
        public override string ToString() =>
            new('x', PropertyValueBudget.MaxStringLength + 1);
    }
}
