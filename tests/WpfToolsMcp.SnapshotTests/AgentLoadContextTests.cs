using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using WpfToolsMcp.Agent;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class AgentLoadContextTests
{
    [Test]
    public void Agent_private_dependencies_are_isolated_while_WPF_is_shared()
    {
        var runtimeAssemblyPath = typeof(AgentRuntimeEntryPoint).Assembly.Location;
        var context = new AgentLoadContext(runtimeAssemblyPath);

        var runtimeAssembly = context.LoadFromAssemblyPath(runtimeAssemblyPath);
        var contractsAssembly = context.LoadFromAssemblyName(typeof(LaunchAppRequest).Assembly.GetName());
        var protocolAssembly = context.LoadFromAssemblyName(new AssemblyName("WpfToolsMcp.AgentProtocol"));
        var snoopAssembly = context.LoadFromAssemblyName(new AssemblyName("Snoop.Core"));
        var presentationFramework = context.LoadFromAssemblyName(typeof(System.Windows.Application).Assembly.GetName());

        Assert.Multiple(() =>
        {
            Assert.That(AssemblyLoadContext.GetLoadContext(runtimeAssembly), Is.SameAs(context));
            Assert.That(AssemblyLoadContext.GetLoadContext(contractsAssembly), Is.SameAs(context));
            Assert.That(contractsAssembly, Is.Not.SameAs(typeof(LaunchAppRequest).Assembly));
            Assert.That(AssemblyLoadContext.GetLoadContext(protocolAssembly), Is.SameAs(context));
            Assert.That(AssemblyLoadContext.GetLoadContext(snoopAssembly), Is.SameAs(context));
            Assert.That(presentationFramework, Is.SameAs(typeof(System.Windows.Application).Assembly));
            Assert.That(AssemblyLoadContext.GetLoadContext(presentationFramework), Is.SameAs(AssemblyLoadContext.Default));
        });
    }

    [Test]
    public void Bootstrap_reports_initialization_failures()
    {
        using var stream = File.OpenRead(typeof(EntryPoint).Assembly.Location);
        var context = new AssemblyLoadContext("agent-bootstrap-without-location", isCollectible: true);
        var bootstrap = context.LoadFromStream(stream);
        var start = bootstrap
            .GetType("WpfToolsMcp.Agent.EntryPoint", throwOnError: true)!
            .GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!;

        var exception = Assert.Throws<TargetInvocationException>(() => start.Invoke(null, ["test-pipe"]));

        Assert.That(exception!.InnerException, Is.TypeOf<InvalidOperationException>());
        Assert.That(exception.InnerException!.Message, Does.Contain("bootstrap directory"));
        context.Unload();
    }

    [Test]
    public void Bootstrap_has_no_references_to_agent_private_dependencies()
    {
        var privateReferences = typeof(EntryPoint).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name =>
                name is not null &&
                (name.StartsWith("WpfToolsMcp.", StringComparison.Ordinal) ||
                 string.Equals(name, "Snoop.Core", StringComparison.Ordinal)))
            .ToArray();

        Assert.That(privateReferences, Is.Empty);
    }

    [Test]
    [NonParallelizable]
    public async Task Injection_ignores_a_conflicting_contracts_assembly_in_the_target_default_context()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        var targetExe = TestAppPaths.FindMinimalTestAppExecutable();
        var conflictAssembly = FindConflictFixture();
        var markerPath = Path.Combine(Path.GetTempPath(), $"wpf-tools-mcp-conflict-{Guid.NewGuid():N}.txt");
        var startInfo = new ProcessStartInfo
        {
            FileName = targetExe,
            WorkingDirectory = Path.GetDirectoryName(targetExe)!,
            UseShellExecute = false
        };
        startInfo.Environment["WPF_TOOLS_MCP_CONFLICT_ASSEMBLY"] = conflictAssembly;
        startInfo.Environment["WPF_TOOLS_MCP_CONFLICT_MARKER"] = markerPath;

        using var target = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the dependency-conflict test app.");
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");
        string? sessionId = null;
        try
        {
            _ = target.WaitForInputIdle(10_000);
            await WaitForFileAsync(markerPath, TimeSpan.FromSeconds(5));
            Assert.That(await File.ReadAllTextAsync(markerPath), Does.Contain("Version=99.0.0.0"));

            var attached = await mcp.CallToolAsync<AttachToAppResponse>(
                "attach_to_app",
                new Dictionary<string, object?> { ["pid"] = target.Id });
            sessionId = attached.SessionId;
            var injection = await mcp.CallToolAsync<InjectAgentResponse>(
                "inject_agent",
                new Dictionary<string, object?> { ["sessionId"] = sessionId });
            var ping = await mcp.CallToolAsync<AgentPingResponse>(
                "agent_ping",
                new Dictionary<string, object?> { ["sessionId"] = sessionId });
            var tree = await mcp.CallToolAsync<GetVisualTreeResponse>(
                "get_visual_tree",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId,
                    ["backend"] = "wpf",
                    ["depth"] = 2,
                    ["maxNodes"] = 100
                });

            Assert.Multiple(() =>
            {
                Assert.That(injection.Injected, Is.True);
                Assert.That(ping.Message, Is.EqualTo("pong").IgnoreCase);
                Assert.That(tree.BackendUsed, Is.EqualTo(InspectionBackend.Wpf));
            });
        }
        finally
        {
            if (sessionId is not null)
            {
                try
                {
                    _ = await mcp.CallToolAsync<CloseAppResponse>(
                        "close_session",
                        new Dictionary<string, object?>
                        {
                            ["sessionId"] = sessionId,
                            ["force"] = true,
                            ["timeoutMs"] = 2_000
                        });
                }
                catch
                {
                }
            }

            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
                await target.WaitForExitAsync();
            }

            File.Delete(markerPath);
        }
    }

    private static string FindConflictFixture()
    {
        var binDirectory = Path.Combine(RepoRoot.Find(), "tests", "WpfToolsMcp.ConflictFixture", "bin");
        return Directory.EnumerateFiles(
                binDirectory,
                "WpfToolsMcp.Contracts.dll",
                SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path) && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(25);
        }

        if (!File.Exists(path))
        {
            throw new AssertionException($"The conflict fixture marker '{path}' was not written.");
        }
    }
}
