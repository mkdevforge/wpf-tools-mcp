using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NUnit.Framework;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
public sealed class ToolProfileTests
{
    private static readonly string[] CoreToolNames =
    [
        "attach_to_app",
        "capture_diagnostic_snapshot",
        "click_element",
        "close_app",
        "close_session",
        "detach_session",
        "drag",
        "find_elements",
        "get_binding_errors",
        "get_binding_info",
        "get_computed_properties",
        "get_data_context",
        "get_element_properties",
        "get_layout_context",
        "get_uia_locators",
        "get_uia_tree",
        "get_validation_errors",
        "get_visual_tree",
        "invoke",
        "launch_app",
        "list_sessions",
        "list_windows",
        "resolve_element",
        "scroll_to_element",
        "select_item",
        "send_keys",
        "set_active_window",
        "set_value",
        "take_screenshot",
        "terminate_app",
        "type_text",
        "wait_for"
    ];

    private static readonly string[] DiagnosticsOnlyToolNames =
    [
        "agent_ping",
        "get_active_window",
        "get_path_to_element",
        "get_style_chain",
        "get_template_info",
        "highlight_element",
        "inject_agent",
        "list_displays",
        "mouse_click",
        "performance_start",
        "performance_stop",
        "pick_element_at_point",
        "poll_subscription",
        "release_element",
        "set_window_bounds",
        "set_window_state",
        "set_window_viewport",
        "subscribe_binding_errors",
        "subscribe_property_changes",
        "trace_start",
        "trace_stop",
        "uia_coverage_report",
        "unsubscribe"
    ];

    [Test]
    public async Task Default_profile_lists_only_core_tools()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(
            serverExe,
            toolProfile: null,
            environmentVariables: new Dictionary<string, string?> { ["WPF_TOOLS_MCP_TOOL_PROFILE"] = null });

        var tools = await mcp.ListToolsAsync();
        var names = tools.Select(t => t.Name).OrderBy(t => t, StringComparer.Ordinal).ToArray();

        Assert.That(names, Is.EqualTo(CoreToolNames));

        foreach (var hidden in DiagnosticsOnlyToolNames)
        {
            Assert.That(names, Does.Not.Contain(hidden), hidden);
        }
    }

    [Test]
    public async Task Diagnostics_profile_lists_full_tool_surface()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");

        var names = (await mcp.ListToolsAsync())
            .Select(t => t.Name)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

        Assert.That(names, Has.Length.EqualTo(CoreToolNames.Length + DiagnosticsOnlyToolNames.Length));

        foreach (var toolName in CoreToolNames.Concat(DiagnosticsOnlyToolNames))
        {
            Assert.That(names, Does.Contain(toolName), toolName);
        }
    }

    [TestCase(null)]
    [TestCase("diagnostics")]
    public async Task Profiles_advertise_output_schemas_for_every_tool(string? toolProfile)
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: toolProfile);

        var tools = await mcp.ListToolsAsync();
        var missingOutputSchemas = tools
            .Where(tool => tool.ProtocolTool.OutputSchema is null)
            .Select(tool => tool.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.That(missingOutputSchemas, Is.Empty);
    }

    [Test]
    public async Task Ordinary_successes_return_structured_content_over_stdio()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var core = await McpTestContext.StartAsync(serverExe, toolProfile: null);
        await using var diagnostics = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");

        var sessions = await core.CallToolResultAsync("list_sessions");
        var displays = await diagnostics.CallToolResultAsync("list_displays");

        AssertStructuredJsonResult(sessions, "sessions");
        AssertStructuredJsonResult(displays, "displays", "virtualScreen");
    }

    [Test]
    public async Task Special_success_returns_declared_schema_and_structured_content_over_stdio()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        var appExe = TestAppPaths.FindTestAppExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: null);
        string? sessionId = null;

        try
        {
            var launchTool = (await mcp.ListToolsAsync()).Single(tool => tool.Name == "launch_app");
            Assert.That(
                GetOutputPropertyNames(launchTool),
                Is.EqualTo(new[] { "interactionPolicy", "pid", "processName", "sessionId" }));

            var result = await mcp.CallToolResultAsync("launch_app", new Dictionary<string, object?>
            {
                ["exePath"] = appExe,
                ["workingDirectory"] = Path.GetDirectoryName(appExe)!
            });
            sessionId = ExtractResultJson(result).GetProperty("sessionId").GetString();

            var structuredContent = AssertStructuredJsonResult(
                result,
                "pid",
                "processName",
                "sessionId");
            Assert.That(structuredContent.GetProperty("sessionId").GetString(), Is.EqualTo(sessionId));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                await CloseSessionBestEffortAsync(mcp, sessionId);
            }
        }
    }

    [Test]
    public async Task Environment_can_select_diagnostics_profile()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(
            serverExe,
            toolProfile: null,
            environmentVariables: new Dictionary<string, string?> { ["WPF_TOOLS_MCP_TOOL_PROFILE"] = "diagnostics" });

        var names = (await mcp.ListToolsAsync()).Select(t => t.Name).ToArray();

        Assert.That(names, Does.Contain("inject_agent"));
        Assert.That(names, Does.Contain("trace_start"));
    }

    [Test]
    public async Task Default_profile_exposes_compact_schemas_for_noisy_tools()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(
            serverExe,
            toolProfile: null,
            environmentVariables: new Dictionary<string, string?> { ["WPF_TOOLS_MCP_TOOL_PROFILE"] = null });

        var tools = (await mcp.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);

        Assert.That(GetInputPropertyNames(tools["take_screenshot"]), Is.EqualTo(
            new[] { "elementId", "includeViewport", "locator", "outputPath", "sessionId", "windowHandle" }));
        Assert.That(GetInputPropertyNames(tools["get_visual_tree"]), Is.EqualTo(
            new[] { "depth", "maxNodes", "root", "sessionId", "visibleOnly", "windowHandle" }));
        Assert.That(GetInputPropertyNames(tools["find_elements"]), Is.EqualTo(
            new[] { "maxResults", "query", "returnFields", "sessionId", "visibleOnly", "windowHandle" }));
        Assert.That(GetInputPropertyNames(tools["get_uia_tree"]), Is.EqualTo(
            new[] { "depth", "maxNodes", "root", "sessionId", "windowHandle" }));
        Assert.That(GetInputPropertyNames(tools["get_element_properties"]), Is.EqualTo(
            new[] { "elementId", "locator", "sessionId", "windowHandle" }));
        Assert.That(GetInputPropertyNames(tools["get_uia_locators"]), Is.EqualTo(
            new[] { "backend", "elementId", "locator", "maxNodes", "sessionId", "windowHandle" }));
        Assert.That(GetInputPropertyNames(tools["get_binding_errors"]), Is.EqualTo(
            new[] { "depth", "rootXPath", "sessionId", "windowHandle" }));
        Assert.That(GetInputPropertyNames(tools["get_validation_errors"]), Is.EqualTo(
            new[] { "depth", "rootXPath", "sessionId", "windowHandle" }));
        Assert.That(GetInputPropertyNames(tools["get_data_context"]), Is.EqualTo(
            new[] { "elementId", "locator", "maxDepth", "properties", "sessionId", "windowHandle" }));
        Assert.That(GetInputPropertyNames(tools["get_computed_properties"]), Is.EqualTo(
            new[] { "elementId", "locator", "propertyNames", "sessionId", "windowHandle" }));
        Assert.That(GetInputPropertyNames(tools["get_layout_context"]), Is.EqualTo(
            new[] { "elementId", "locator", "sessionId", "windowHandle" }));
        Assert.That(GetInputPropertyNames(tools["capture_diagnostic_snapshot"]), Is.EqualTo(
            new[]
            {
                "budget",
                "dataContextProperties",
                "elementId",
                "locator",
                "propertyNames",
                "sections",
                "sessionId",
                "timeoutMs",
                "windowHandle"
            }));
        Assert.That(GetInputPropertyNames(tools["click_element"]), Is.EqualTo(
            new[] { "clickType", "elementId", "interactionPolicy", "locator", "sessionId" }));
        Assert.That(GetInputPropertyNames(tools["type_text"]), Is.EqualTo(
            new[] { "elementId", "interactionPolicy", "locator", "mode", "sessionId", "text" }));
        Assert.That(GetInputPropertyNames(tools["send_keys"]), Is.EqualTo(
            new[] { "elementId", "interactionPolicy", "locator", "sequence", "sessionId" }));
        Assert.That(GetInputPropertyNames(tools["drag"]), Is.EqualTo(
            new[] { "elementId", "interactionPolicy", "locator", "sessionId", "targetElementId", "targetLocator", "toX", "toY" }));
        Assert.That(
            GetOutputPropertyNames(tools["resolve_element"]),
            Is.EqualTo(new[] { "backendUsed", "element", "fallback", "windowHandleUsed" }));
        Assert.That(
            GetOutputPropertyNames(tools["get_uia_locators"]),
            Is.EqualTo(new[] { "flaUi", "locatorSuggestions", "uia", "uiaMapping", "wpf" }));

        foreach (var toolName in new[]
                 {
                     "take_screenshot",
                     "get_visual_tree",
                     "find_elements",
                     "get_uia_tree",
                     "get_element_properties",
                     "get_binding_errors",
                     "get_validation_errors",
                     "get_data_context",
                     "get_computed_properties",
                     "get_layout_context",
                     "capture_diagnostic_snapshot",
                     "click_element",
                     "type_text",
                     "drag"
                 })
        {
            Assert.That(
                tools[toolName].JsonSchema.GetRawText().Length,
                Is.LessThanOrEqualTo(4096),
                $"{toolName} input schema exceeded the compact-profile character budget.");
        }
    }

    [Test]
    public async Task Property_provenance_controls_are_diagnostics_only()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var core = await McpTestContext.StartAsync(serverExe, toolProfile: "core");
        await using var diagnostics = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");

        var coreTools = (await core.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var diagnosticTools = (await diagnostics.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var coreInputs = GetInputPropertyNames(coreTools["get_computed_properties"]);
        var diagnosticInputs = GetInputPropertyNames(diagnosticTools["get_computed_properties"]);

        Assert.Multiple(() =>
        {
            Assert.That(coreInputs, Does.Not.Contain("includeProvenance"));
            Assert.That(coreInputs, Does.Not.Contain("maxProvenanceCandidates"));
            Assert.That(diagnosticInputs, Does.Contain("includeProvenance"));
            Assert.That(diagnosticInputs, Does.Contain("maxProvenanceCandidates"));
        });
    }

    [Test]
    public async Task Screenshot_correlation_controls_are_diagnostics_only()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var core = await McpTestContext.StartAsync(serverExe, toolProfile: "core");
        await using var diagnostics = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");

        var coreTools = (await core.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var diagnosticTools = (await diagnostics.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var coreInputs = GetInputPropertyNames(coreTools["take_screenshot"]);
        var diagnosticInputs = GetInputPropertyNames(diagnosticTools["take_screenshot"]);

        Assert.Multiple(() =>
        {
            Assert.That(coreInputs, Is.EqualTo(
                new[] { "elementId", "includeViewport", "locator", "outputPath", "sessionId", "windowHandle" }));
            Assert.That(coreInputs, Does.Not.Contain("correlation"));
            Assert.That(diagnosticInputs, Does.Contain("correlation"));
            Assert.That(
                GetInputObjectPropertyNames(diagnosticTools["take_screenshot"], "correlation"),
                Is.EqualTo(new[]
                {
                    "annotate",
                    "backend",
                    "height",
                    "includeAncestors",
                    "maxAncestors",
                    "maxCandidates",
                    "maxNodes",
                    "width",
                    "x",
                    "y"
                }));
            Assert.That(
                GetInputObjectEnumValues(diagnosticTools["take_screenshot"], "correlation", "backend"),
                Is.EqualTo(new[] { "Auto", "Both", "Uia", "Wpf" }));
        });
    }

    [Test]
    public async Task Keyboard_input_tools_expose_discoverable_structured_schemas()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var core = await McpTestContext.StartAsync(serverExe, toolProfile: "core");
        await using var diagnostics = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");

        var coreTools = (await core.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var diagnosticTools = (await diagnostics.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var expectedModes = Enum.GetNames<TextEntryMode>().OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var expectedKeys = Enum.GetNames<KeyboardKey>().OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var expectedModifiers = Enum.GetNames<KeyboardModifier>().OrderBy(value => value, StringComparer.Ordinal).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(GetInputPropertyNames(coreTools["type_text"]), Is.EqualTo(
                new[] { "elementId", "interactionPolicy", "locator", "mode", "sessionId", "text" }));
            Assert.That(GetInputPropertyNames(diagnosticTools["type_text"]), Is.EqualTo(
                new[]
                {
                    "autoWait",
                    "elementId",
                    "interactionPolicy",
                    "locator",
                    "mode",
                    "pollIntervalMs",
                    "sessionId",
                    "stableMs",
                    "text",
                    "timeoutMs",
                    "windowHandle"
                }));
            Assert.That(GetInputEnumValues(coreTools["type_text"], "mode"), Is.EqualTo(expectedModes));
            Assert.That(GetInputEnumValues(diagnosticTools["type_text"], "mode"), Is.EqualTo(expectedModes));

            Assert.That(GetInputPropertyNames(coreTools["send_keys"]), Is.EqualTo(
                new[] { "elementId", "interactionPolicy", "locator", "sequence", "sessionId" }));
            Assert.That(GetInputPropertyNames(diagnosticTools["send_keys"]), Is.EqualTo(
                new[]
                {
                    "autoWait",
                    "elementId",
                    "interactionPolicy",
                    "locator",
                    "pollIntervalMs",
                    "sequence",
                    "sessionId",
                    "stableMs",
                    "timeoutMs",
                    "windowHandle"
                }));
            Assert.That(GetInputArrayItemObjectPropertyNames(coreTools["send_keys"], "sequence"),
                Is.EqualTo(new[] { "key", "modifiers" }));
            Assert.That(GetInputArrayItemObjectRequiredPropertyNames(coreTools["send_keys"], "sequence"),
                Is.EqualTo(new[] { "key" }));
            Assert.That(GetInputArrayItemObjectEnumValues(coreTools["send_keys"], "sequence", "key"),
                Is.EqualTo(expectedKeys));
            Assert.That(GetInputArrayItemObjectEnumValues(coreTools["send_keys"], "sequence", "modifiers"),
                Is.EqualTo(expectedModifiers));
        });
    }

    [Test]
    public async Task Wait_for_exposes_discoverable_structured_condition_schemas_in_both_profiles()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var core = await McpTestContext.StartAsync(serverExe, toolProfile: "core");
        await using var diagnostics = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");

        var coreWait = (await core.ListToolsAsync()).Single(tool => tool.Name == "wait_for");
        var diagnosticsWait = (await diagnostics.ListToolsAsync()).Single(tool => tool.Name == "wait_for");
        var expectedConditionKinds = Enum.GetNames<WaitConditionKind>().OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var expectedComparisons = Enum.GetNames<WaitComparison>().OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var expectedScalarKinds = Enum.GetNames<WaitScalarKind>().OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var expectedScalarVariants = new Dictionary<string, (string[] Properties, string[] Required)>(StringComparer.Ordinal)
        {
            [nameof(WaitScalarKind.String)] = (["kind", "stringValue"], ["stringValue"]),
            [nameof(WaitScalarKind.Number)] = (["kind", "numberValue"], ["numberValue"]),
            [nameof(WaitScalarKind.Boolean)] = (["booleanValue", "kind"], ["booleanValue"]),
            [nameof(WaitScalarKind.Null)] = (["kind"], [])
        };
        var expectedComparisonScalarKinds = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [nameof(WaitComparison.Equals)] = expectedScalarKinds,
            [nameof(WaitComparison.NotEquals)] = expectedScalarKinds,
            [nameof(WaitComparison.Contains)] = [nameof(WaitScalarKind.String)],
            [nameof(WaitComparison.GreaterThan)] = [nameof(WaitScalarKind.Number)],
            [nameof(WaitComparison.GreaterThanOrEqual)] = [nameof(WaitScalarKind.Number)],
            [nameof(WaitComparison.LessThan)] = [nameof(WaitScalarKind.Number)],
            [nameof(WaitComparison.LessThanOrEqual)] = [nameof(WaitScalarKind.Number)]
        };
        var expectedVariants = new Dictionary<string, (string[] Properties, string[] Required)>(StringComparer.Ordinal)
        {
            [nameof(WaitConditionKind.Attached)] = (["kind"], []),
            [nameof(WaitConditionKind.Visible)] = (["kind"], []),
            [nameof(WaitConditionKind.Enabled)] = (["kind"], []),
            [nameof(WaitConditionKind.Actionable)] = (["kind"], []),
            [nameof(WaitConditionKind.BoundsStable)] = (["holdForMs", "kind"], []),
            [nameof(WaitConditionKind.NumericValueEquals)] =
                (["comparison", "expected", "kind"], ["expected"]),
            [nameof(WaitConditionKind.NameContains)] =
                (["comparison", "expected", "kind"], ["expected"]),
            [nameof(WaitConditionKind.DependencyPropertyValue)] =
                (["comparison", "expected", "holdForMs", "kind", "propertyName"], ["expected", "propertyName"]),
            [nameof(WaitConditionKind.DataContextValue)] =
                (["comparison", "dataContextPath", "expected", "holdForMs", "kind"], ["dataContextPath", "expected"]),
            [nameof(WaitConditionKind.WindowOpen)] = (["kind", "window"], ["window"]),
            [nameof(WaitConditionKind.WindowClosed)] = (["kind", "window"], ["window"])
        };

        Assert.Multiple(() =>
        {
            Assert.That(GetInputPropertyNames(coreWait), Is.EqualTo(
                new[]
                {
                    "condition",
                    "elementId",
                    "expectedText",
                    "expectedValue",
                    "locator",
                    "sessionId",
                    "state",
                    "throwOnTimeout",
                    "timeoutMs"
                }));
            Assert.That(GetInputPropertyNames(diagnosticsWait), Is.EqualTo(
                new[]
                {
                    "backend",
                    "condition",
                    "elementId",
                    "expectedText",
                    "expectedValue",
                    "locator",
                    "pollIntervalMs",
                    "sessionId",
                    "stableMs",
                    "state",
                    "throwOnTimeout",
                    "timeoutMs",
                    "windowHandle"
                }));

            foreach (var waitTool in new[] { coreWait, diagnosticsWait })
            {
                Assert.That(GetInputObjectRequiredPropertyNames(waitTool, "condition"), Is.EqualTo(new[] { "kind" }));
                var variants = GetInputObjectDiscriminatedVariants(waitTool, "condition", "kind");
                Assert.That(
                    variants.Keys.OrderBy(value => value, StringComparer.Ordinal),
                    Is.EqualTo(expectedConditionKinds));

                foreach (var (kind, expected) in expectedVariants)
                {
                    Assert.That(GetObjectPropertyNames(variants[kind]), Is.EqualTo(expected.Properties), kind);
                    Assert.That(GetObjectRequiredPropertyNames(variants[kind]), Is.EqualTo(expected.Required), kind);
                    Assert.That(
                        variants[kind].GetProperty("additionalProperties").GetBoolean(),
                        Is.False,
                        kind);
                }

                foreach (var (valueKind, pathPropertyName) in new[]
                         {
                             (nameof(WaitConditionKind.DependencyPropertyValue), "propertyName"),
                             (nameof(WaitConditionKind.DataContextValue), "dataContextPath")
                         })
                {
                    var valueVariant = variants[valueKind];
                    Assert.That(GetObjectEnumValues(valueVariant, "comparison"), Is.EqualTo(expectedComparisons));
                    var expectedSchema = GetObjectPropertySchema(valueVariant, "expected");
                    Assert.That(GetObjectRequiredPropertyNames(expectedSchema), Is.EqualTo(new[] { "kind" }));
                    var scalarVariants = GetObjectDiscriminatedVariants(expectedSchema, "kind");
                    Assert.That(
                        scalarVariants.Keys.OrderBy(value => value, StringComparer.Ordinal),
                        Is.EqualTo(expectedScalarKinds));
                    foreach (var (kind, expected) in expectedScalarVariants)
                    {
                        Assert.That(GetObjectPropertyNames(scalarVariants[kind]), Is.EqualTo(expected.Properties), kind);
                        Assert.That(GetObjectRequiredPropertyNames(scalarVariants[kind]), Is.EqualTo(expected.Required), kind);
                        Assert.That(
                            scalarVariants[kind].GetProperty("additionalProperties").GetBoolean(),
                            Is.False,
                            kind);
                    }

                    var comparisonScalarKinds = GetComparisonScalarKindConstraints(valueVariant);
                    Assert.That(comparisonScalarKinds.Keys, Is.EquivalentTo(expectedComparisons));
                    foreach (var (comparison, scalarKinds) in expectedComparisonScalarKinds)
                    {
                        Assert.That(comparisonScalarKinds[comparison], Is.EqualTo(scalarKinds), comparison);
                    }

                    Assert.That(
                        GetComparisonConstraintRequiredProperties(valueVariant, nameof(WaitComparison.Equals)),
                        Does.Not.Contain("comparison"));
                    Assert.That(
                        GetComparisonConstraintRequiredProperties(valueVariant, nameof(WaitComparison.Contains)),
                        Does.Contain("comparison"));

                    var pathSchema = GetObjectPropertySchema(valueVariant, pathPropertyName);
                    Assert.That(pathSchema.GetProperty("minLength").GetInt32(), Is.EqualTo(1));
                    Assert.That(pathSchema.GetProperty("pattern").GetString(), Is.EqualTo("\\S"));
                    AssertIntegerBounds(valueVariant, "holdForMs", minimum: 0, maximum: 5_000);
                }

                AssertIntegerBounds(
                    variants[nameof(WaitConditionKind.BoundsStable)],
                    "holdForMs",
                    minimum: 0,
                    maximum: 5_000);

                var numericVariant = variants[nameof(WaitConditionKind.NumericValueEquals)];
                Assert.That(GetObjectConstValue(numericVariant, "comparison"), Is.EqualTo(nameof(WaitComparison.Equals)));
                Assert.That(
                    GetObjectDiscriminatedVariants(GetObjectPropertySchema(numericVariant, "expected"), "kind").Keys,
                    Is.EquivalentTo(new[] { nameof(WaitScalarKind.Number) }));

                var nameVariant = variants[nameof(WaitConditionKind.NameContains)];
                Assert.That(GetObjectConstValue(nameVariant, "comparison"), Is.EqualTo(nameof(WaitComparison.Contains)));
                Assert.That(
                    GetObjectDiscriminatedVariants(GetObjectPropertySchema(nameVariant, "expected"), "kind").Keys,
                    Is.EquivalentTo(new[] { nameof(WaitScalarKind.String) }));

                var windowSchema = GetObjectPropertySchema(
                    variants[nameof(WaitConditionKind.WindowOpen)],
                    "window");
                Assert.That(GetObjectPropertyNames(windowSchema), Is.EqualTo(
                    new[] { "frameworkId", "handle", "ownerHandle", "title", "titleContains" }));
                Assert.That(windowSchema.GetProperty("additionalProperties").GetBoolean(), Is.False);
                Assert.That(GetAnyOfSingleRequiredProperties(windowSchema), Is.EqualTo(
                    new[] { "frameworkId", "handle", "ownerHandle", "title", "titleContains" }));
            }
        });
    }

    [TestCase("core")]
    [TestCase("diagnostics")]
    public async Task Wait_for_rejects_legacy_state_combined_with_structured_condition(string profile)
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: profile);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            _ = await mcp.CallToolAsync<WaitForResponse>("wait_for", new Dictionary<string, object?>
            {
                ["sessionId"] = "unused",
                ["state"] = "visible",
                ["condition"] = new Dictionary<string, object?> { ["kind"] = "Visible" }
            }));

        Assert.That(exception!.Message, Does.Contain("invalid_request"));
    }

    [TestCase("core")]
    [TestCase("diagnostics")]
    public async Task Wait_for_accepts_condition_discriminator_after_variant_fields(string profile)
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: profile);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            _ = await mcp.CallToolAsync<WaitForResponse>("wait_for", new Dictionary<string, object?>
            {
                ["sessionId"] = "unused",
                ["state"] = "visible",
                ["condition"] = new Dictionary<string, object?>
                {
                    ["expected"] = new Dictionary<string, object?>
                    {
                        ["kind"] = WaitScalarKind.String.ToString(),
                        ["stringValue"] = "Ready"
                    },
                    ["kind"] = WaitConditionKind.NameContains.ToString()
                }
            }));

        Assert.That(exception!.Message, Does.Contain("invalid_request"));
    }

    [Test]
    public async Task Core_profile_exposes_explicit_session_lifecycle_schemas()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: "core");
        var tools = (await mcp.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(GetInputPropertyNames(tools["detach_session"]), Is.EqualTo(new[] { "sessionId" }));
            Assert.That(GetInputPropertyNames(tools["close_app"]), Is.EqualTo(new[] { "sessionId", "timeoutMs" }));
            Assert.That(GetInputPropertyNames(tools["terminate_app"]), Is.EqualTo(new[] { "sessionId", "timeoutMs" }));
            Assert.That(GetInputPropertyNames(tools["close_session"]), Is.EqualTo(new[] { "force", "sessionId" }));
        });
    }

    [Test]
    public async Task Diagnostics_profile_exposes_explicit_session_lifecycle_schemas()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");
        var tools = (await mcp.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(GetInputPropertyNames(tools["detach_session"]), Is.EqualTo(new[] { "sessionId" }));
            Assert.That(GetInputPropertyNames(tools["close_app"]), Is.EqualTo(new[] { "sessionId", "timeoutMs" }));
            Assert.That(GetInputPropertyNames(tools["terminate_app"]), Is.EqualTo(new[] { "sessionId", "timeoutMs" }));
            Assert.That(GetInputPropertyNames(tools["close_session"]), Is.EqualTo(new[] { "force", "sessionId", "timeoutMs" }));
        });
    }

    [TestCase(null)]
    [TestCase("diagnostics")]
    public async Task Process_selection_schemas_support_structured_ambiguity(string? toolProfile)
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: toolProfile);
        var tools = await mcp.ListToolsAsync();
        var attach = tools.Single(candidate => candidate.Name == "attach_to_app");
        var launch = tools.Single(candidate => candidate.Name == "launch_app");

        Assert.Multiple(() =>
        {
            Assert.That(
                GetInputPropertyNames(attach),
                Is.EqualTo(new[] { "interactionPolicy", "pid", "processInstanceId", "processName", "sessionId" }));
            Assert.That(
                GetOutputPropertyNames(attach),
                Is.EqualTo(new[]
                {
                    "activeWindow",
                    "interactionPolicy",
                    "pid",
                    "processInstanceId",
                    "processName",
                    "recovery",
                    "sessionId"
                }));
            Assert.That(
                GetOutputPropertyNames(launch),
                Is.EqualTo(new[] { "interactionPolicy", "pid", "processName", "sessionId" }));
        });
    }

    [Test]
    public async Task Core_profile_exposes_interaction_policy_on_session_and_action_tools()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(
            serverExe,
            toolProfile: null,
            environmentVariables: new Dictionary<string, string?> { ["WPF_TOOLS_MCP_TOOL_PROFILE"] = null });

        var tools = (await mcp.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var policyAwareTools = new[]
        {
            "launch_app",
            "attach_to_app",
            "set_active_window",
            "click_element",
            "invoke",
            "type_text",
            "send_keys",
            "set_value",
            "select_item",
            "scroll_to_element",
            "drag"
        };

        foreach (var toolName in policyAwareTools)
        {
            Assert.That(
                GetInputPropertyNames(tools[toolName]),
                Does.Contain("interactionPolicy"),
                $"{toolName} should expose a per-session or per-operation interaction policy.");
            Assert.That(
                GetInputObjectPropertyNames(tools[toolName], "interactionPolicy"),
                Is.EqualTo(new[] { "allowForegroundActivation", "allowPhysicalInput" }),
                $"{toolName} should expose the complete nested interaction policy.");
        }
    }

    [Test]
    public async Task Diagnostics_profile_exposes_interaction_policy_on_all_action_tools()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");

        var tools = (await mcp.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var policyAwareTools = new[]
        {
            "launch_app",
            "attach_to_app",
            "set_active_window",
            "set_window_bounds",
            "set_window_state",
            "set_window_viewport",
            "click_element",
            "mouse_click",
            "invoke",
            "type_text",
            "send_keys",
            "set_value",
            "select_item",
            "scroll_to_element",
            "drag"
        };

        foreach (var toolName in policyAwareTools)
        {
            Assert.That(
                GetInputPropertyNames(tools[toolName]),
                Does.Contain("interactionPolicy"),
                $"{toolName} should expose a per-session or per-operation interaction policy.");
            Assert.That(
                GetInputObjectPropertyNames(tools[toolName], "interactionPolicy"),
                Is.EqualTo(new[] { "allowForegroundActivation", "allowPhysicalInput" }),
                $"{toolName} should expose the complete nested interaction policy.");
        }
    }

    [Test]
    public async Task Diagnostics_profile_exposes_deterministic_viewport_control_schema()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");

        var tools = (await mcp.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);

        Assert.That(GetInputPropertyNames(tools["set_window_viewport"]), Is.EqualTo(
            new[]
            {
                "clampToWorkArea",
                "clientHeight",
                "clientWidth",
                "ensureForeground",
                "interactionPolicy",
                "sessionId",
                "unit",
                "windowHandle"
            }));
    }

    [Test]
    public async Task Launch_policy_is_resolved_and_reported_by_the_session()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        var appExe = TestAppPaths.FindTestAppExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: null);

        var launch = await mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = appExe,
            ["workingDirectory"] = Path.GetDirectoryName(appExe)!,
            ["interactionPolicy"] = new Dictionary<string, object?>
            {
                ["allowForegroundActivation"] = false
            }
        });

        try
        {
            Assert.That(launch.InteractionPolicy, Is.Not.Null);
            Assert.That(launch.InteractionPolicy!.AllowForegroundActivation, Is.False);
            Assert.That(launch.InteractionPolicy.AllowPhysicalInput, Is.True);

            var sessions = await mcp.CallToolAsync<ListSessionsResponse>(
                "list_sessions",
                new Dictionary<string, object?>());
            var session = sessions.Sessions.Single(item => item.SessionId == launch.SessionId);

            Assert.That(session.InteractionPolicy, Is.EqualTo(launch.InteractionPolicy));
        }
        finally
        {
            await CloseSessionBestEffortAsync(mcp, launch.SessionId);
        }
    }

    [Test]
    public async Task Diagnostics_profile_exposes_explicit_evidence_expansion_controls()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");

        var tools = (await mcp.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);

        Assert.That(GetInputPropertyNames(tools["get_element_properties"]), Does.Contain("preset"));
        Assert.That(GetInputPropertyNames(tools["get_element_properties"]), Does.Contain("maxProperties"));
        Assert.That(GetInputPropertyNames(tools["get_uia_locators"]), Is.EqualTo(
            new[] { "backend", "elementId", "locator", "maxNodes", "sessionId", "windowHandle" }));
        Assert.That(GetInputPropertyNames(tools["get_layout_context"]), Does.Contain("maxAncestors"));
        Assert.That(GetInputPropertyNames(tools["get_layout_context"]), Does.Contain("maxSiblings"));
        Assert.That(GetInputPropertyNames(tools["get_layout_context"]), Does.Contain("maxGridDefinitions"));
        Assert.That(GetInputPropertyNames(tools["trace_stop"]), Does.Contain("includeEvents"));
        Assert.That(GetInputPropertyNames(tools["trace_stop"]), Does.Contain("maxEvents"));
        Assert.That(GetInputPropertyNames(tools["get_validation_errors"]), Is.EqualTo(
            new[]
            {
                "depth",
                "maxErrors",
                "maxNodes",
                "maxValueLength",
                "rootXPath",
                "sessionId",
                "visibleOnly",
                "windowHandle"
            }));
    }

    [Test]
    public async Task Default_profile_auto_injects_for_visual_tree()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(
            serverExe,
            toolProfile: null,
            environmentVariables: new Dictionary<string, string?> { ["WPF_TOOLS_MCP_TOOL_PROFILE"] = null });

        var launch = await LaunchPrimaryTestAppAsync(mcp);
        try
        {
            var tree = await mcp.CallToolAsync<GetVisualTreeResponse>("get_visual_tree", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["depth"] = 2,
                ["maxNodes"] = 100
            });

            Assert.That(tree.BackendUsed, Is.EqualTo(InspectionBackend.Wpf));
            Assert.That(tree.Warnings, Is.Null.Or.Empty);
        }
        finally
        {
            await CloseSessionBestEffortAsync(mcp, launch.SessionId);
        }
    }

    [Test]
    public async Task Default_profile_falls_back_to_uia_when_auto_injection_assets_are_missing()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        var isolatedServerExe = CopyServerWithoutPhase2Payload(serverExe);
        var isolatedServerDir = Path.GetDirectoryName(isolatedServerExe)!;

        try
        {
            await using var mcp = await McpTestContext.StartAsync(
                isolatedServerExe,
                toolProfile: null,
                environmentVariables: new Dictionary<string, string?> { ["WPF_TOOLS_MCP_TOOL_PROFILE"] = null });

            var launch = await LaunchPrimaryTestAppAsync(mcp);
            try
            {
                var sessionsBeforeFallback = await mcp.CallToolAsync<ListSessionsResponse>("list_sessions");
                var sessionBeforeFallback = sessionsBeforeFallback.Sessions.Single(session => session.SessionId == launch.SessionId);
                var wpfBeforeFallback = sessionBeforeFallback.BackendCapabilityStates.Single(state => state.Backend == "wpf");
                Assert.Multiple(() =>
                {
                    Assert.That(wpfBeforeFallback.State, Is.EqualTo("not_initialized"));
                    Assert.That(wpfBeforeFallback.Failure, Is.Null);
                });

                var tree = await mcp.CallToolAsync<GetVisualTreeResponse>("get_visual_tree", new Dictionary<string, object?>
                {
                    ["sessionId"] = launch.SessionId,
                    ["depth"] = 2,
                    ["maxNodes"] = 100
                });

                var matches = await mcp.CallToolAsync<FindElementsResponse>("find_elements", new Dictionary<string, object?>
                {
                    ["sessionId"] = launch.SessionId,
                    ["query"] = new Dictionary<string, object?> { ["automationId"] = "Basic_Button" },
                    ["maxResults"] = 3
                });

                var resolved = await mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
                {
                    ["sessionId"] = launch.SessionId,
                    ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_Button" }
                });

                var sessionsAfterFallback = await mcp.CallToolAsync<ListSessionsResponse>("list_sessions");
                var sessionAfterFallback = sessionsAfterFallback.Sessions.Single(session => session.SessionId == launch.SessionId);
                var wpfAfterFallback = sessionAfterFallback.BackendCapabilityStates.Single(state => state.Backend == "wpf");

                Assert.Multiple(() =>
                {
                    Assert.That(tree.BackendUsed, Is.EqualTo(InspectionBackend.Uia));
                    Assert.That(matches.BackendUsed, Is.EqualTo(InspectionBackend.Uia));
                    Assert.That(matches.ReturnedMatches, Is.EqualTo(1));
                    Assert.That(resolved.BackendUsed, Is.EqualTo(InspectionBackend.Uia));
                    Assert.That(resolved.Element.AutomationId, Is.EqualTo("Basic_Button"));
                    Assert.That(wpfAfterFallback.State, Is.EqualTo("unavailable"));
                });

                AssertMissingAssetsFallback(tree.Fallback, attempted: true);
                AssertMissingAssetsFallback(matches.Fallback, attempted: false);
                AssertMissingAssetsFallback(resolved.Fallback, attempted: false);
                AssertMissingAssetsFailure(wpfAfterFallback.Failure);

                Assert.That(tree.Warnings, Is.Not.Null);
                Assert.That(matches.Warnings, Is.Not.Null);
                Assert.That(string.Join(" ", tree.Warnings!), Does.Contain("backend_assets_missing at injection"));
                Assert.That(string.Join(" ", matches.Warnings!), Does.Contain("backend_assets_missing at injection"));

                var serializedResponses = JsonSerializer.Serialize(new
                {
                    sessionsBeforeFallback,
                    tree,
                    matches,
                    resolved,
                    sessionsAfterFallback
                });
                Assert.That(serializedResponses, Does.Not.Contain(isolatedServerDir).IgnoreCase);

                var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    _ = await mcp.CallToolAsync<GetDataContextResponse>("get_data_context", new Dictionary<string, object?>
                    {
                        ["sessionId"] = launch.SessionId,
                        ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_Button" }
                    }));

                Assert.Multiple(() =>
                {
                    Assert.That(ex!.Message, Does.Contain("backend_assets_missing"));
                    Assert.That(ex.Message, Does.Not.Contain(isolatedServerDir).IgnoreCase);
                });
            }
            finally
            {
                await CloseSessionBestEffortAsync(mcp, launch.SessionId);
            }
        }
        finally
        {
            TryDeleteDirectory(isolatedServerDir);
        }
    }

    [Test]
    public async Task Default_profile_supports_basic_agent_flow()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(
            serverExe,
            toolProfile: null,
            environmentVariables: new Dictionary<string, string?> { ["WPF_TOOLS_MCP_TOOL_PROFILE"] = null });

        var launch = await LaunchPrimaryTestAppAsync(mcp);
        try
        {
            var minimalMatches = await mcp.CallToolAsync<FindElementsResponse>("find_elements", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["query"] = new Dictionary<string, object?> { ["automationId"] = "Basic_Button" },
                ["maxResults"] = 3
            });

            Assert.That(minimalMatches.ReturnedMatches, Is.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(minimalMatches.Matches[0].ClassName, Is.Null);
                Assert.That(minimalMatches.Matches[0].Bounds, Is.Null);
                Assert.That(minimalMatches.Matches[0].IsVisible, Is.Null);
                Assert.That(minimalMatches.Matches[0].IsOffscreen, Is.Null);
            });

            var matches = await mcp.CallToolAsync<FindElementsResponse>("find_elements", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["query"] = new Dictionary<string, object?> { ["automationId"] = "Basic_Button" },
                ["maxResults"] = 3,
                ["returnFields"] = "standard"
            });

            Assert.Multiple(() =>
            {
                Assert.That(matches.ReturnedMatches, Is.EqualTo(1));
                Assert.That(matches.DiscoveredMatches, Is.EqualTo(1));
                Assert.That(matches.Matches[0].Type, Is.EqualTo("Button"));
                Assert.That(matches.Matches[0].ClassName, Is.Not.Null.And.Not.Empty);
                Assert.That(matches.Matches[0].Bounds, Is.Not.Null);
                Assert.That(matches.Matches[0].IsVisible, Is.True);
            });

            var sessions = await mcp.CallToolAsync<ListSessionsResponse>("list_sessions");
            Assert.That(sessions.Sessions.Select(s => s.SessionId), Does.Contain(launch.SessionId));

            var windows = await mcp.CallToolAsync<ListWindowsResponse>("list_windows", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId
            });
            Assert.That(windows.Windows, Is.Not.Empty);

            var focus = await mcp.CallToolAsync<FocusWindowResponse>("set_active_window", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["windowHandle"] = windows.Windows[0].Handle
            });
            Assert.That(focus.Focused, Is.True);

            var resolved = await mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_Button" }
            });

            Assert.That(resolved.BackendUsed, Is.EqualTo(InspectionBackend.Wpf));
            Assert.That(resolved.Element.ElementId, Is.Not.Null.And.Not.Empty);

            var properties = await mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["elementId"] = resolved.Element.ElementId
            });

            Assert.That(properties.Element.AutomationId, Is.EqualTo("Basic_Button"));

            var subtree = await mcp.CallToolAsync<GetVisualTreeResponse>("get_visual_tree", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["root"] = new Dictionary<string, object?> { ["automationId"] = "Basic_Button" },
                ["depth"] = 1,
                ["maxNodes"] = 10
            });

            Assert.That(subtree.BackendUsed, Is.EqualTo(InspectionBackend.Wpf));
            Assert.That(subtree.Root.AutomationId, Is.EqualTo("Basic_Button"));

            var click = await mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["elementId"] = resolved.Element.ElementId
            });

            Assert.That(click.Clicked, Is.True);

            var screenshot = await mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_Button" }
            });

            Assert.That(File.Exists(screenshot.Path), Is.True);
            Assert.That(screenshot.RequestedBounds, Is.Not.Null);
            File.Delete(screenshot.Path);
        }
        finally
        {
            await CloseSessionBestEffortAsync(mcp, launch.SessionId);
        }
    }

    [Test]
    public async Task Default_profile_attach_to_app_by_pid()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        var exePath = TestAppPaths.FindTestAppExecutable();
        Process? app = null;
        string? attachedSessionId = null;

        await using var mcp = await McpTestContext.StartAsync(
            serverExe,
            toolProfile: null,
            environmentVariables: new Dictionary<string, string?> { ["WPF_TOOLS_MCP_TOOL_PROFILE"] = null });

        try
        {
            app = StartExternalApp(exePath);

            var attach = await mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["pid"] = app.Id
            });
            attachedSessionId = attach.SessionId;

            Assert.That(attach.Pid, Is.EqualTo(app.Id));

            var windows = await mcp.CallToolAsync<ListWindowsResponse>("list_windows", new Dictionary<string, object?>
            {
                ["sessionId"] = attachedSessionId
            });
            Assert.That(windows.Windows.Select(w => w.Title), Does.Contain("WPF Tools MCP TestApp"));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(attachedSessionId))
            {
                await CloseSessionBestEffortAsync(mcp, attachedSessionId);
            }

            KillProcessBestEffort(app);
        }
    }

    [Test]
    public async Task Default_profile_supports_core_interaction_tools()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(
            serverExe,
            toolProfile: null,
            environmentVariables: new Dictionary<string, string?> { ["WPF_TOOLS_MCP_TOOL_PROFILE"] = null });

        var launch = await LaunchPrimaryTestAppAsync(mcp);
        try
        {
            var visible = await mcp.CallToolAsync<WaitForResponse>("wait_for", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_Button" },
                ["state"] = "visible"
            });
            Assert.That(visible.Succeeded, Is.True);

            var invoke = await mcp.CallToolAsync<InvokeResponse>("invoke", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_Button" }
            });
            Assert.That(invoke.Invoked, Is.True);

            var clickedStatus = await mcp.CallToolAsync<WaitForResponse>("wait_for", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_ClickStatus" },
                ["state"] = "name_contains",
                ["expectedText"] = "Clicks: 1"
            });
            Assert.That(clickedStatus.Succeeded, Is.True);

            var typed = await mcp.CallToolAsync<TypeTextResponse>("type_text", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_TextBox" },
                ["text"] = "Default profile typed text"
            });
            Assert.That(typed.Typed, Is.True);

            var textBox = await mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_TextBox" }
            });
            Assert.That(GetPatternValue(textBox, "Value", "Value")?.GetValue<string>(), Is.EqualTo("Default profile typed text"));

            var set = await mcp.CallToolAsync<SetValueResponse>("set_value", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_Slider" },
                ["value"] = 70
            });
            Assert.That(set.Set, Is.True);

            var slider = await mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_Slider" }
            });
            Assert.That(GetPatternValue(slider, "RangeValue", "Value")?.GetValue<double>(), Is.EqualTo(70).Within(0.5));

            var selected = await mcp.CallToolAsync<SelectItemResponse>("select_item", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_ListBox" },
                ["text"] = "Item 10"
            });
            Assert.That(selected.Selected, Is.True);

            var listBoxStatus = await mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_ListBoxStatus" }
            });
            Assert.That(listBoxStatus.Element.Name, Does.Contain("Item 10"));

            var sliderTree = await mcp.CallToolAsync<GetVisualTreeResponse>("get_visual_tree", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["root"] = new Dictionary<string, object?> { ["automationId"] = "Basic_Slider" },
                ["depth"] = 8,
                ["maxNodes"] = 120
            });
            var thumbXPath = FindFirstXPathByType(sliderTree.Root, "Thumb");
            Assert.That(thumbXPath, Is.Not.Null.And.Not.Empty);

            var thumb = await mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["xpath"] = thumbXPath }
            });

            var sliderBounds = slider.Element.Bounds;
            var drag = await mcp.CallToolAsync<DragResponse>("drag", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["elementId"] = thumb.Element.ElementId,
                ["toX"] = sliderBounds.X + sliderBounds.Width - 4,
                ["toY"] = sliderBounds.Y + sliderBounds.Height / 2
            });
            Assert.That(drag.Dragged, Is.True);
        }
        finally
        {
            await CloseSessionBestEffortAsync(mcp, launch.SessionId);
        }
    }

    [Test]
    public async Task Default_profile_supports_scroll_to_element()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(
            serverExe,
            toolProfile: null,
            environmentVariables: new Dictionary<string, string?> { ["WPF_TOOLS_MCP_TOOL_PROFILE"] = null });

        var launch = await LaunchAppAsync(mcp, TestAppPaths.FindScrollTestAppExecutable());
        try
        {
            var scroll = await mcp.CallToolAsync<ScrollToElementResponse>("scroll_to_element", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Scroll_TargetButton" }
            });

            Assert.That(scroll.Scrolled, Is.True);

            var after = await mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Scroll_TargetButton" }
            });
            Assert.That(after.Element.IsOffscreen, Is.False);
        }
        finally
        {
            await CloseSessionBestEffortAsync(mcp, launch.SessionId);
        }
    }

    [Test]
    public async Task Default_profile_supports_wpf_diagnostic_tools()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(
            serverExe,
            toolProfile: null,
            environmentVariables: new Dictionary<string, string?> { ["WPF_TOOLS_MCP_TOOL_PROFILE"] = null });

        var launch = await LaunchAppAsync(mcp, TestAppPaths.FindBindingErrorsTestAppExecutable());
        try
        {
            var bindingErrors = await mcp.CallToolAsync<GetBindingErrorsResponse>("get_binding_errors", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["depth"] = 8
            });
            Assert.That(bindingErrors.Errors, Is.Not.Empty);

            var bindingInfo = await mcp.CallToolAsync<GetBindingInfoResponse>("get_binding_info", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "BindingErrors_OkTextBox" }
            });
            Assert.That(bindingInfo.Element.AutomationId, Is.EqualTo("BindingErrors_OkTextBox"));
            Assert.That(bindingInfo.Bindings.Select(b => b.TargetProperty), Does.Contain("Text"));

            var dataContext = await mcp.CallToolAsync<GetDataContextResponse>("get_data_context", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "BindingErrors_OkTextBox" },
                ["maxDepth"] = 1,
                ["properties"] = new[] { "OkText" }
            });
            Assert.That(dataContext.DataContextType, Does.Contain("MainViewModel"));

            var computed = await mcp.CallToolAsync<GetComputedPropertiesResponse>("get_computed_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = "BindingErrors_OkTextBox" },
                ["propertyNames"] = new[] { "Text", "Width" }
            });
            Assert.That(computed.Element.AutomationId, Is.EqualTo("BindingErrors_OkTextBox"));
            Assert.That(computed.Properties.Select(p => p.Name), Does.Contain("Text"));
        }
        finally
        {
            await CloseSessionBestEffortAsync(mcp, launch.SessionId);
        }
    }

    private static string[] GetInputPropertyNames(McpClientTool tool)
    {
        var schema = tool.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return properties
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] GetOutputPropertyNames(McpClientTool tool)
    {
        if (tool.ProtocolTool.OutputSchema is not { } schema ||
            schema.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (schema.TryGetProperty("oneOf", out var branches) &&
            branches.ValueKind == JsonValueKind.Array)
        {
            schema = branches.EnumerateArray().FirstOrDefault(branch =>
                branch.ValueKind == JsonValueKind.Object &&
                !branch.TryGetProperty("error", out _) &&
                (!branch.TryGetProperty("required", out var required) ||
                 required.ValueKind != JsonValueKind.Array ||
                 !required.EnumerateArray().Any(item => item.GetString() == "error")));
        }

        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return properties
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static JsonElement AssertStructuredJsonResult(
        CallToolResult result,
        params string[] expectedProperties)
    {
        Assert.That(result.IsError, Is.Not.True);
        Assert.That(result.StructuredContent, Is.Not.Null);

        var structuredContent = result.StructuredContent!.Value;
        Assert.That(structuredContent.ValueKind, Is.EqualTo(JsonValueKind.Object));

        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        using var compatibilityDocument = JsonDocument.Parse(text);
        Assert.Multiple(() =>
        {
            Assert.That(compatibilityDocument.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(JsonElement.DeepEquals(compatibilityDocument.RootElement, structuredContent), Is.True);
            foreach (var propertyName in expectedProperties)
            {
                Assert.That(
                    structuredContent.TryGetProperty(propertyName, out _),
                    Is.True,
                    $"Structured result omitted '{propertyName}'.");
            }
        });

        return structuredContent;
    }

    private static JsonElement ExtractResultJson(CallToolResult result)
    {
        if (result.StructuredContent is { } structuredContent)
        {
            return structuredContent;
        }

        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static string[] GetInputObjectPropertyNames(McpClientTool tool, string inputPropertyName)
    {
        var schema = tool.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var inputProperties) ||
            inputProperties.ValueKind != JsonValueKind.Object ||
            !inputProperties.TryGetProperty(inputPropertyName, out var inputProperty) ||
            inputProperty.ValueKind != JsonValueKind.Object ||
            !inputProperty.TryGetProperty("properties", out var nestedProperties) ||
            nestedProperties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return nestedProperties
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] GetInputObjectRequiredPropertyNames(McpClientTool tool, string inputPropertyName)
    {
        var schema = tool.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var inputProperties) ||
            inputProperties.ValueKind != JsonValueKind.Object ||
            !inputProperties.TryGetProperty(inputPropertyName, out var inputProperty) ||
            inputProperty.ValueKind != JsonValueKind.Object ||
            !inputProperty.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return GetSortedStringValues(required);
    }

    private static IReadOnlyDictionary<string, JsonElement> GetInputObjectDiscriminatedVariants(
        McpClientTool tool,
        string inputPropertyName,
        string discriminatorPropertyName)
    {
        var schema = tool.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var inputProperties) ||
            inputProperties.ValueKind != JsonValueKind.Object ||
            !inputProperties.TryGetProperty(inputPropertyName, out var inputProperty) ||
            inputProperty.ValueKind != JsonValueKind.Object ||
            !inputProperty.TryGetProperty("anyOf", out var variants) ||
            variants.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var variant in variants.EnumerateArray())
        {
            if (variant.ValueKind != JsonValueKind.Object ||
                !variant.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object ||
                !properties.TryGetProperty(discriminatorPropertyName, out var discriminator) ||
                discriminator.ValueKind != JsonValueKind.Object ||
                !discriminator.TryGetProperty("const", out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            result.Add(value.GetString()!, variant.Clone());
        }

        return result;
    }

    private static string[] GetObjectPropertyNames(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return properties
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] GetObjectRequiredPropertyNames(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return GetSortedStringValues(required);
    }

    private static IReadOnlyDictionary<string, JsonElement> GetObjectDiscriminatedVariants(
        JsonElement schema,
        string discriminatorPropertyName)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("anyOf", out var variants) ||
            variants.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var variant in variants.EnumerateArray())
        {
            if (variant.ValueKind != JsonValueKind.Object ||
                !variant.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object ||
                !properties.TryGetProperty(discriminatorPropertyName, out var discriminator) ||
                discriminator.ValueKind != JsonValueKind.Object ||
                !discriminator.TryGetProperty("const", out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            result.Add(value.GetString()!, variant.Clone());
        }

        return result;
    }

    private static string[] GetObjectEnumValues(JsonElement schema, string propertyName)
    {
        var property = GetObjectPropertySchema(schema, propertyName);
        return property.ValueKind == JsonValueKind.Object &&
               property.TryGetProperty("enum", out var values) &&
               values.ValueKind == JsonValueKind.Array
            ? GetSortedStringValues(values)
            : [];
    }

    private static string? GetObjectConstValue(JsonElement schema, string propertyName)
    {
        var property = GetObjectPropertySchema(schema, propertyName);
        return property.ValueKind == JsonValueKind.Object &&
               property.TryGetProperty("const", out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string[] GetAnyOfSingleRequiredProperties(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("anyOf", out var variants) ||
            variants.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return variants
            .EnumerateArray()
            .SelectMany(GetObjectRequiredPropertyNames)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string[]> GetComparisonScalarKindConstraints(JsonElement schema)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("anyOf", out var variants) ||
            variants.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var variant in variants.EnumerateArray())
        {
            var scalarKinds = GetObjectDiscriminatedVariants(
                    GetObjectPropertySchema(variant, "expected"),
                    "kind")
                .Keys
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            foreach (var comparison in GetObjectEnumValues(variant, "comparison"))
            {
                result.Add(comparison, scalarKinds);
            }
        }

        return result;
    }

    private static string[] GetComparisonConstraintRequiredProperties(JsonElement schema, string comparison)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("anyOf", out var variants) ||
            variants.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var variant in variants.EnumerateArray())
        {
            if (GetObjectEnumValues(variant, "comparison").Contains(comparison, StringComparer.Ordinal))
            {
                return GetObjectRequiredPropertyNames(variant);
            }
        }

        return [];
    }

    private static void AssertIntegerBounds(
        JsonElement schema,
        string propertyName,
        int minimum,
        int maximum)
    {
        var property = GetObjectPropertySchema(schema, propertyName);
        Assert.Multiple(() =>
        {
            Assert.That(property.GetProperty("type").GetString(), Is.EqualTo("integer"));
            Assert.That(property.GetProperty("minimum").GetInt32(), Is.EqualTo(minimum));
            Assert.That(property.GetProperty("maximum").GetInt32(), Is.EqualTo(maximum));
        });
    }

    private static JsonElement GetObjectPropertySchema(JsonElement schema, string propertyName)
    {
        if (schema.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object &&
            properties.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Object)
        {
            return property;
        }

        return default;
    }

    private static string[] GetInputNestedObjectPropertyNames(
        McpClientTool tool,
        string inputPropertyName,
        string nestedPropertyName)
    {
        if (!TryGetInputNestedObjectSchema(tool, inputPropertyName, nestedPropertyName, out var nestedObject) ||
            !nestedObject.TryGetProperty("properties", out var nestedProperties) ||
            nestedProperties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return nestedProperties
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] GetInputNestedObjectRequiredPropertyNames(
        McpClientTool tool,
        string inputPropertyName,
        string nestedPropertyName)
    {
        if (!TryGetInputNestedObjectSchema(tool, inputPropertyName, nestedPropertyName, out var nestedObject) ||
            !nestedObject.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return GetSortedStringValues(required);
    }

    private static string[] GetInputNestedObjectEnumValues(
        McpClientTool tool,
        string inputPropertyName,
        string nestedPropertyName,
        string valuePropertyName)
    {
        if (!TryGetInputNestedObjectSchema(tool, inputPropertyName, nestedPropertyName, out var nestedObject) ||
            !nestedObject.TryGetProperty("properties", out var nestedProperties) ||
            nestedProperties.ValueKind != JsonValueKind.Object ||
            !nestedProperties.TryGetProperty(valuePropertyName, out var valueProperty) ||
            valueProperty.ValueKind != JsonValueKind.Object ||
            !valueProperty.TryGetProperty("enum", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return GetSortedStringValues(values);
    }

    private static bool TryGetInputNestedObjectSchema(
        McpClientTool tool,
        string inputPropertyName,
        string nestedPropertyName,
        out JsonElement nestedObject)
    {
        var schema = tool.JsonSchema;
        if (schema.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("properties", out var inputProperties) &&
            inputProperties.ValueKind == JsonValueKind.Object &&
            inputProperties.TryGetProperty(inputPropertyName, out var inputProperty) &&
            inputProperty.ValueKind == JsonValueKind.Object &&
            inputProperty.TryGetProperty("properties", out var inputObjectProperties) &&
            inputObjectProperties.ValueKind == JsonValueKind.Object &&
            inputObjectProperties.TryGetProperty(nestedPropertyName, out nestedObject) &&
            nestedObject.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        nestedObject = default;
        return false;
    }

    private static string[] GetInputEnumValues(McpClientTool tool, string inputPropertyName)
    {
        var schema = tool.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var inputProperties) ||
            inputProperties.ValueKind != JsonValueKind.Object ||
            !inputProperties.TryGetProperty(inputPropertyName, out var inputProperty) ||
            inputProperty.ValueKind != JsonValueKind.Object ||
            !inputProperty.TryGetProperty("enum", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return GetSortedStringValues(values);
    }

    private static string[] GetInputArrayItemObjectPropertyNames(
        McpClientTool tool,
        string inputPropertyName)
    {
        var schema = tool.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var inputProperties) ||
            inputProperties.ValueKind != JsonValueKind.Object ||
            !inputProperties.TryGetProperty(inputPropertyName, out var inputProperty) ||
            inputProperty.ValueKind != JsonValueKind.Object ||
            !inputProperty.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Object ||
            !items.TryGetProperty("properties", out var itemProperties) ||
            itemProperties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return itemProperties
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] GetInputArrayItemObjectRequiredPropertyNames(
        McpClientTool tool,
        string inputPropertyName)
    {
        var schema = tool.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var inputProperties) ||
            inputProperties.ValueKind != JsonValueKind.Object ||
            !inputProperties.TryGetProperty(inputPropertyName, out var inputProperty) ||
            inputProperty.ValueKind != JsonValueKind.Object ||
            !inputProperty.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Object ||
            !items.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return GetSortedStringValues(required);
    }

    private static string[] GetInputArrayItemObjectEnumValues(
        McpClientTool tool,
        string inputPropertyName,
        string itemPropertyName)
    {
        var schema = tool.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var inputProperties) ||
            inputProperties.ValueKind != JsonValueKind.Object ||
            !inputProperties.TryGetProperty(inputPropertyName, out var inputProperty) ||
            inputProperty.ValueKind != JsonValueKind.Object ||
            !inputProperty.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Object ||
            !items.TryGetProperty("properties", out var itemProperties) ||
            itemProperties.ValueKind != JsonValueKind.Object ||
            !itemProperties.TryGetProperty(itemPropertyName, out var itemProperty) ||
            itemProperty.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (itemProperty.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            return GetSortedStringValues(values);
        }

        if (itemProperty.TryGetProperty("items", out var arrayItems) &&
            arrayItems.ValueKind == JsonValueKind.Object &&
            arrayItems.TryGetProperty("enum", out values) &&
            values.ValueKind == JsonValueKind.Array)
        {
            return GetSortedStringValues(values);
        }

        return [];
    }

    private static string[] GetSortedStringValues(JsonElement values) =>
        values
            .EnumerateArray()
            .Select(value => value.GetString())
            .Where(value => value is not null)
            .Select(value => value!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static string[] GetInputObjectEnumValues(
        McpClientTool tool,
        string inputPropertyName,
        string nestedPropertyName)
    {
        var schema = tool.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var inputProperties) ||
            inputProperties.ValueKind != JsonValueKind.Object ||
            !inputProperties.TryGetProperty(inputPropertyName, out var inputProperty) ||
            inputProperty.ValueKind != JsonValueKind.Object ||
            !inputProperty.TryGetProperty("properties", out var nestedProperties) ||
            nestedProperties.ValueKind != JsonValueKind.Object ||
            !nestedProperties.TryGetProperty(nestedPropertyName, out var nestedProperty) ||
            nestedProperty.ValueKind != JsonValueKind.Object ||
            !nestedProperty.TryGetProperty("enum", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values
            .EnumerateArray()
            .Select(value => value.GetString())
            .Where(value => value is not null)
            .Select(value => value!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<LaunchAppResponse> LaunchPrimaryTestAppAsync(McpTestContext mcp)
        => await LaunchAppAsync(mcp, TestAppPaths.FindTestAppExecutable());

    private static async Task<LaunchAppResponse> LaunchAppAsync(McpTestContext mcp, string exePath)
    {
        return await mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = Path.GetDirectoryName(exePath)!
        });
    }

    private static Process StartExternalApp(string exePath)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = false
        });

        if (process is null)
        {
            throw new InvalidOperationException("Failed to start test app process.");
        }

        _ = process.WaitForInputIdle(10_000);
        return process;
    }

    private static void KillProcessBestEffort(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
        }
        catch
        {
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch
            {
            }
        }
    }

    private static JsonNode? GetPatternValue(GetElementPropertiesResponse response, string patternName, string valueName)
    {
        if (!response.Patterns.TryGetValue(patternName, out var pattern) ||
            pattern is not JsonObject patternObject ||
            patternObject["values"] is not JsonObject values ||
            !values.TryGetPropertyValue(valueName, out var value))
        {
            return null;
        }

        return value;
    }

    private static string? FindFirstXPathByType(TreeNode node, string type)
    {
        if (string.Equals(node.Type, type, StringComparison.OrdinalIgnoreCase))
        {
            return node.XPath;
        }

        foreach (var child in node.Children)
        {
            var match = FindFirstXPathByType(child, type);
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return null;
    }

    private static async Task CloseSessionBestEffortAsync(McpTestContext mcp, string sessionId)
    {
        try
        {
            _ = await mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["force"] = true
            });
        }
        catch
        {
        }
    }

    private static void AssertMissingAssetsFallback(BackendFallbackInfo? fallback, bool attempted)
    {
        Assert.That(fallback, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(fallback!.FromBackend, Is.EqualTo("wpf"));
            Assert.That(fallback.ToBackend, Is.EqualTo("uia"));
            Assert.That(fallback.Attempted, Is.EqualTo(attempted));
            Assert.That(fallback.Available, Is.True);
            Assert.That(fallback.Used, Is.True);
        });
        AssertMissingAssetsFailure(fallback!.Failure);
    }

    private static void AssertMissingAssetsFailure(FailureInfo? failure)
    {
        Assert.That(failure, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(failure!.Code, Is.EqualTo("backend_assets_missing"));
            Assert.That(failure.Stage, Is.EqualTo("injection"));
            Assert.That(failure.Detail, Is.EqualTo("Required WPF backend files are unavailable."));
            Assert.That(failure.Retryable, Is.False);
            Assert.That(failure.RetryAfterMs, Is.Null);
            Assert.That(failure.RecoveryActions, Is.EqualTo(new[] { "use_uia", "repair_installation" }));
        });
    }

    private static string CopyServerWithoutPhase2Payload(string serverExe)
    {
        var sourceDir = Path.GetDirectoryName(serverExe)!;
        var destinationDir = Path.Combine(Path.GetTempPath(), "wpf-tools-mcp-no-agent-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(sourceDir, destinationDir, skipPhase2Payload: true);
        return Path.Combine(destinationDir, Path.GetFileName(serverExe));
    }

    private static void CopyDirectory(string sourceDir, string destinationDir, bool skipPhase2Payload)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(directory);
            if (skipPhase2Payload &&
                (name.Equals("agent", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("snoop", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            CopyDirectory(directory, Path.Combine(destinationDir, name), skipPhase2Payload);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
