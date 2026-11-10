using System.Text.Json.Nodes;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

/// <summary>
/// Drives a server that runs on another machine, for example the QA VM from
/// scripts/vm, over HTTP. The test copies the test application into the shared
/// folder and uses host paths only, so it checks the path map too.
/// </summary>
/// <remarks>
/// Set these variables to run it; otherwise it is ignored:
/// WPF_TOOLS_MCP_REMOTE_URL (for example http://192.168.208.128:5765/mcp),
/// WPF_TOOLS_MCP_REMOTE_TOKEN, and WPF_TOOLS_MCP_REMOTE_SHARED_DIR (the host
/// folder that the server maps). The server must use the diagnostics profile.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class RemoteServerSmokeTests
{
    private static readonly Dictionary<string, object?> PhysicalInput = new()
    {
        ["allowForegroundActivation"] = true,
        ["allowPhysicalInput"] = true
    };

    [Test]
    public async Task Remote_server_drives_the_test_app_and_publishes_host_paths()
    {
        var url = Environment.GetEnvironmentVariable("WPF_TOOLS_MCP_REMOTE_URL");
        var token = Environment.GetEnvironmentVariable("WPF_TOOLS_MCP_REMOTE_TOKEN");
        var sharedDirectory = Environment.GetEnvironmentVariable("WPF_TOOLS_MCP_REMOTE_SHARED_DIR");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(sharedDirectory))
        {
            Assert.Ignore("Set WPF_TOOLS_MCP_REMOTE_URL, WPF_TOOLS_MCP_REMOTE_TOKEN, and WPF_TOOLS_MCP_REMOTE_SHARED_DIR to run this test.");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;

        // The application under test comes from the shared folder, under its host path.
        var sourceDirectory = Path.GetDirectoryName(TestAppPaths.FindTestAppExecutable())!;
        var appDirectory = Path.Combine(sharedDirectory!, "apps", $"WpfToolsMcp.TestApp-{Guid.NewGuid():N}");
        CopyDirectory(sourceDirectory, appDirectory);

        await using var mcp = await McpTestContext.ConnectHttpAsync(new Uri(url!), token, ct);
        string? sessionId = null;
        try
        {
            var environment = await mcp.CallToolAsync<GetServerEnvironmentResponse>("get_server_environment", cancellationToken: ct);
            Assert.That(environment.InteractiveDesktop.Available, Is.True, environment.InteractiveDesktop.Detail);
            Assert.That(environment.InteractiveDesktop.SessionId, Is.GreaterThan(0));

            var launched = await mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
            {
                ["exePath"] = Path.Combine(appDirectory, "WpfToolsMcp.TestApp.exe"),
                ["reuseExistingInstance"] = false,
                ["interactionPolicy"] = PhysicalInput
            }, ct);
            sessionId = launched.SessionId;

            // Explicit injection into a fresh process injects the agent.
            var injection = await mcp.CallToolAsync<InjectAgentResponse>("inject_agent", Args(sessionId), ct);
            Assert.That(injection.Injected, Is.True);
            var dataContext = await mcp.CallToolAsync<JsonObject>("get_data_context", Args(sessionId, ("locator", Locator("Basic_TextBox"))), ct);
            Assert.That(dataContext["element"]?["automationId"]?.GetValue<string>(), Is.EqualTo("Basic_TextBox"));

            // Physical mouse input.
            var button = await GetPropertiesAsync(mcp, sessionId, "Basic_Button", ct);
            var mouse = await mcp.CallToolAsync<JsonObject>("mouse_click", Args(sessionId,
                ("x", (int)(button.Element.Bounds.X + button.Element.Bounds.Width / 2)),
                ("y", (int)(button.Element.Bounds.Y + button.Element.Bounds.Height / 2)),
                ("interactionPolicy", PhysicalInput)), ct);
            Assert.That(mouse["effects"]?["mouseInput"]?.GetValue<bool>(), Is.True);
            await WaitForAsync(async () => (await GetPropertiesAsync(mcp, sessionId, "Basic_ClickStatus", ct)).Element.Name == "Clicks: 1", ct);

            // Physical keyboard input.
            var textBox = await GetPropertiesAsync(mcp, sessionId, "Basic_TextBox", ct);
            _ = await mcp.CallToolAsync<JsonObject>("mouse_click", Args(sessionId,
                ("x", (int)(textBox.Element.Bounds.X + textBox.Element.Bounds.Width / 2)),
                ("y", (int)(textBox.Element.Bounds.Y + textBox.Element.Bounds.Height / 2)),
                ("interactionPolicy", PhysicalInput)), ct);
            var keys = await mcp.CallToolAsync<JsonObject>("send_keys", Args(sessionId,
                ("locator", Locator("Basic_TextBox")),
                ("interactionPolicy", PhysicalInput),
                ("sequence", new object[]
                {
                    new Dictionary<string, object?> { ["key"] = "End" },
                    new Dictionary<string, object?> { ["key"] = "Space" },
                    new Dictionary<string, object?> { ["key"] = "Q" },
                    new Dictionary<string, object?> { ["key"] = "A", ["modifiers"] = new[] { "Shift" } }
                })), ct);
            Assert.That(keys["effects"]?["keyboardInput"]?.GetValue<bool>(), Is.True);
            await WaitForAsync(async () =>
                TextValue(await GetPropertiesAsync(mcp, sessionId, "Basic_TextBox", ct))?.EndsWith(" qA", StringComparison.Ordinal) == true, ct);

            // Both capture modes write to the shared folder and report host paths.
            foreach (var mode in new[] { "screen", "printWindow" })
            {
                var shot = await mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", Args(sessionId, ("captureMode", mode)), ct);
                Assert.That(shot.Path, Does.StartWith(sharedDirectory), mode);
                Assert.That(new FileInfo(shot.Path).Length, Is.GreaterThan(4096), mode);
            }

            var sequence = await mcp.CallToolAsync<TakeScreenshotSequenceResponse>("take_screenshot_sequence",
                Args(sessionId, ("frameCount", 3), ("intervalMs", 50)), ct);
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(sequence.ManifestPath, ct))!;
            var frames = manifest["frames"]!.AsArray().Select(frame => (string)frame!["path"]!).ToArray();
            Assert.That(frames, Has.Length.EqualTo(3));
            Assert.That(frames.All(File.Exists), Is.True, string.Join(", ", frames));

            var trace = await mcp.CallToolAsync<TraceStartResponse>("trace_start", Args(sessionId), ct);
            var stopped = await mcp.CallToolAsync<TraceStopResponse>("trace_stop", Args(sessionId, ("traceId", trace.TraceId)), ct);
            Assert.That(File.Exists(stopped.OutputPath), Is.True, stopped.OutputPath);
        }
        finally
        {
            if (sessionId is not null)
            {
                await mcp.CallToolResultAsync("close_app", Args(sessionId), CancellationToken.None);
            }

            await DeleteDirectoryAsync(appDirectory);
        }
    }

    private static Dictionary<string, object?> Args(string sessionId, params (string Name, object? Value)[] values)
    {
        var arguments = new Dictionary<string, object?> { ["sessionId"] = sessionId };
        foreach (var (name, value) in values)
        {
            arguments[name] = value;
        }

        return arguments;
    }

    private static Dictionary<string, object?> Locator(string automationId) => new() { ["automationId"] = automationId };

    private static Task<GetElementPropertiesResponse> GetPropertiesAsync(
        McpTestContext mcp,
        string sessionId,
        string automationId,
        CancellationToken ct) =>
        mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", Args(sessionId, ("locator", Locator(automationId))), ct);

    private static string? TextValue(GetElementPropertiesResponse properties) =>
        properties.Patterns.TryGetValue("Value", out var pattern) ? pattern?["values"]?["Value"]?.GetValue<string>() : null;

    private static async Task WaitForAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The expected application state did not appear within 5 seconds.");
            }

            await Task.Delay(100, ct);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    private static async Task DeleteDirectoryAsync(string path)
    {
        // The guest can hold the files for a moment after the application exits.
        for (var attempt = 0; attempt < 20 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(500);
            }
        }
    }
}
