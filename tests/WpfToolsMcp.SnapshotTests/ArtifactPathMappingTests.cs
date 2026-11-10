using System.Text.Json.Nodes;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

/// <summary>
/// A server that runs in a VM sees a shared folder under a different path than
/// the client. These tests map a server folder to a client drive that does not
/// exist on this machine (Q:), and check that the server writes artifacts to the
/// server folder and publishes them under the client path.
/// </summary>
[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class ArtifactPathMappingTests
{
    private const string ClientArtifacts = @"Q:\qa-artifacts";
    private const string ClientApp = @"Q:\qa-app";

    [Test]
    public async Task Server_writes_to_server_paths_and_publishes_client_paths()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = cts.Token;
        var exePath = TestAppPaths.FindTestAppExecutable();
        var appDirectory = Path.GetDirectoryName(exePath)!;
        var serverArtifacts = Path.Combine(Path.GetTempPath(), $"wpf-tools-mcp-artifacts-{Guid.NewGuid():N}");
        string ToServer(string clientPath) => serverArtifacts + clientPath[ClientArtifacts.Length..];

        var mcp = await McpTestContext.StartAsync(
            McpServerPaths.FindMcpServerExecutable(),
            toolProfile: "diagnostics",
            environmentVariables: new Dictionary<string, string?>
            {
                ["WPF_TOOLS_MCP_ARTIFACT_DIR"] = serverArtifacts,
                ["WPF_TOOLS_MCP_SCREENSHOT_DIR"] = null,
                ["WPF_TOOLS_MCP_PATH_MAP"] = $"{serverArtifacts}={ClientArtifacts}|{appDirectory}={ClientApp}"
            },
            cancellationToken: token);
        string? sessionId = null;
        try
        {
            var launched = await mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
            {
                ["exePath"] = Path.Combine(ClientApp, Path.GetFileName(exePath)),
                ["workingDirectory"] = ClientApp,
                ["reuseExistingInstance"] = false
            }, token);
            sessionId = launched.SessionId;

            var screenshot = await mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId
            }, token);
            Assert.That(screenshot.Path, Does.StartWith(ClientArtifacts + @"\screenshots\"));
            Assert.That(File.Exists(ToServer(screenshot.Path)), Is.True, screenshot.Path);

            var requestedPath = ClientArtifacts + @"\custom\shot.png";
            var named = await mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["outputPath"] = requestedPath
            }, token);
            Assert.That(named.Path, Is.EqualTo(requestedPath));
            Assert.That(File.Exists(ToServer(requestedPath)), Is.True);

            var sequence = await mcp.CallToolAsync<TakeScreenshotSequenceResponse>("take_screenshot_sequence", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["frameCount"] = 2,
                ["intervalMs"] = 50
            }, token);
            Assert.Multiple(() =>
            {
                Assert.That(sequence.DirectoryPath, Does.StartWith(ClientArtifacts + @"\screenshots\sequence-"));
                Assert.That(sequence.ManifestPath, Is.EqualTo(sequence.DirectoryPath + @"\manifest.json"));
                Assert.That(sequence.FirstFramePath, Does.StartWith(sequence.DirectoryPath));
                Assert.That(File.Exists(ToServer(sequence.LastFramePath)), Is.True);
            });
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(ToServer(sequence.ManifestPath), token))!;
            var framePaths = manifest["frames"]!.AsArray().Select(frame => (string)frame!["path"]!).ToArray();
            Assert.That(framePaths, Has.Length.EqualTo(2).And.All.StartWith(sequence.DirectoryPath));

            var trace = await mcp.CallToolAsync<TraceStartResponse>("trace_start", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId
            }, token);
            var stopped = await mcp.CallToolAsync<TraceStopResponse>("trace_stop", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["traceId"] = trace.TraceId
            }, token);
            Assert.That(stopped.OutputPath, Does.StartWith(ClientArtifacts + @"\traces\"));
            Assert.That(File.Exists(ToServer(stopped.OutputPath)), Is.True);
        }
        finally
        {
            if (sessionId is not null)
            {
                await mcp.CallToolResultAsync("close_app", new Dictionary<string, object?> { ["sessionId"] = sessionId }, CancellationToken.None);
            }

            await mcp.DisposeAsync();
            try
            {
                Directory.Delete(serverArtifacts, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
