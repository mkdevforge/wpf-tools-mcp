using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using NUnit.Framework;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
public sealed class ToolProfileTests
{
    private static readonly string[] CoreToolNames =
    [
        "attach_to_app",
        "click_element",
        "close_session",
        "drag",
        "find_elements",
        "get_binding_errors",
        "get_binding_info",
        "get_computed_properties",
        "get_data_context",
        "get_element_properties",
        "get_visual_tree",
        "invoke",
        "launch_app",
        "list_sessions",
        "list_windows",
        "resolve_element",
        "scroll_to_element",
        "select_item",
        "set_active_window",
        "set_value",
        "take_screenshot",
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
        "subscribe_binding_errors",
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
            environmentVariables: new Dictionary<string, string?> { ["WPFPILOT_TOOL_PROFILE"] = null });

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

        Assert.That(names, Has.Length.EqualTo(44));

        foreach (var toolName in CoreToolNames.Concat(DiagnosticsOnlyToolNames))
        {
            Assert.That(names, Does.Contain(toolName), toolName);
        }
    }

    [Test]
    public async Task Environment_can_select_diagnostics_profile()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(
            serverExe,
            toolProfile: null,
            environmentVariables: new Dictionary<string, string?> { ["WPFPILOT_TOOL_PROFILE"] = "diagnostics" });

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
            environmentVariables: new Dictionary<string, string?> { ["WPFPILOT_TOOL_PROFILE"] = null });

        var tools = (await mcp.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);

        Assert.That(GetInputPropertyCount(tools["take_screenshot"]), Is.LessThanOrEqualTo(5));
        Assert.That(GetInputPropertyCount(tools["get_visual_tree"]), Is.LessThanOrEqualTo(6));
        Assert.That(GetInputPropertyCount(tools["click_element"]), Is.LessThanOrEqualTo(4));
        Assert.That(GetInputPropertyCount(tools["drag"]), Is.LessThanOrEqualTo(7));
    }

    [Test]
    public async Task Default_profile_auto_injects_for_visual_tree()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(
            serverExe,
            toolProfile: null,
            environmentVariables: new Dictionary<string, string?> { ["WPFPILOT_TOOL_PROFILE"] = null });

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
                environmentVariables: new Dictionary<string, string?> { ["WPFPILOT_TOOL_PROFILE"] = null });

            var launch = await LaunchPrimaryTestAppAsync(mcp);
            try
            {
                var tree = await mcp.CallToolAsync<GetVisualTreeResponse>("get_visual_tree", new Dictionary<string, object?>
                {
                    ["sessionId"] = launch.SessionId,
                    ["depth"] = 2,
                    ["maxNodes"] = 100
                });

                Assert.That(tree.BackendUsed, Is.EqualTo(InspectionBackend.Uia));
                Assert.That(tree.Warnings, Is.Not.Null);
                Assert.That(string.Join(" ", tree.Warnings!), Does.Contain("WPF auto-injection failed"));

                var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    _ = await mcp.CallToolAsync<GetDataContextResponse>("get_data_context", new Dictionary<string, object?>
                    {
                        ["sessionId"] = launch.SessionId,
                        ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Basic_Button" }
                    }));

                Assert.That(ex!.Message, Does.Contain("Phase 2 agent payload directory not found"));
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
            environmentVariables: new Dictionary<string, string?> { ["WPFPILOT_TOOL_PROFILE"] = null });

        var launch = await LaunchPrimaryTestAppAsync(mcp);
        try
        {
            var matches = await mcp.CallToolAsync<FindElementsResponse>("find_elements", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["query"] = new Dictionary<string, object?> { ["automationId"] = "Basic_Button" },
                ["maxResults"] = 3
            });

            Assert.That(matches.ReturnedMatches, Is.EqualTo(1));

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
            environmentVariables: new Dictionary<string, string?> { ["WPFPILOT_TOOL_PROFILE"] = null });

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
            Assert.That(windows.Windows.Select(w => w.Title), Does.Contain("WpfPilot TestApp"));
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
            environmentVariables: new Dictionary<string, string?> { ["WPFPILOT_TOOL_PROFILE"] = null });

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
            environmentVariables: new Dictionary<string, string?> { ["WPFPILOT_TOOL_PROFILE"] = null });

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
            environmentVariables: new Dictionary<string, string?> { ["WPFPILOT_TOOL_PROFILE"] = null });

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

    private static int GetInputPropertyCount(McpClientTool tool)
    {
        var schema = tool.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        return properties.EnumerateObject().Count();
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
