using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class InjectionSnapshots
{
    [Test]
    public async Task Agent_reconnect_after_mcp_restart_snapshot()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        var exePath = TestAppPaths.FindTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        Process? app = null;
        McpTestContext? mcp1 = null;
        McpTestContext? mcp2 = null;

        try
        {
            app = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            });

            if (app is null)
            {
                throw new InvalidOperationException("Failed to start test app process.");
            }

            _ = app.WaitForInputIdle(10_000);

            mcp1 = await McpTestContext.StartAsync(serverExe);

            _ = await mcp1.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["pid"] = app.Id
            });

            InjectAgentResponse inject1;
            try
            {
                inject1 = await mcp1.CallToolAsync<InjectAgentResponse>("inject_agent");
            }
            catch (InvalidOperationException ex) when (ShouldSkipForMissingAssets(ex))
            {
                Assert.Ignore(ex.Message);
                return;
            }

            var pong1 = await mcp1.CallToolAsync<AgentPingResponse>("agent_ping");
            Assert.That(pong1.Message, Is.EqualTo("pong").IgnoreCase);

            var uiaTree1 = await mcp1.CallToolAsync<GetVisualTreeResponse>("get_visual_tree", new Dictionary<string, object?>
            {
                ["depth"] = 10
            });

            var wpfTree1 = await mcp1.CallToolAsync<GetWpfVisualTreeResponse>("get_wpf_visual_tree", new Dictionary<string, object?>
            {
                ["depth"] = 12
            });

            // Simulate MCP server restart (new process, new AutomationController instance).
            await mcp1.DisposeAsync();
            mcp1 = null;

            mcp2 = await McpTestContext.StartAsync(serverExe);
            _ = await mcp2.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["pid"] = app.Id
            });

            var inject2 = await mcp2.CallToolAsync<InjectAgentResponse>("inject_agent");
            Assert.That(inject2.Injected, Is.False, "Expected reconnect without reinjection after MCP restart.");
            Assert.That(inject2.PipeName, Is.EqualTo(inject1.PipeName));

            var pong2 = await mcp2.CallToolAsync<AgentPingResponse>("agent_ping");
            Assert.That(pong2.Message, Is.EqualTo("pong").IgnoreCase);

            var wpfTree2 = await mcp2.CallToolAsync<GetWpfVisualTreeResponse>("get_wpf_visual_tree", new Dictionary<string, object?>
            {
                ["depth"] = 12
            });

            var stable = new
            {
                Pid = -1,
                PipeName = "<scrubbed>",
                FirstInjected = inject1.Injected,
                SecondInjected = inject2.Injected,
                Pong1 = pong1.Message,
                Pong2 = pong2.Message,
                WpfRootType = wpfTree1.Root.Type,
                UiaAutomationIds = CollectUiaAutomationIds(uiaTree1.Root).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                WpfAutomationIds1 = CollectWpfAutomationIds(wpfTree1.Root).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                WpfAutomationIds2 = CollectWpfAutomationIds(wpfTree2.Root).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            };

            await Verifier.Verify(stable);
        }
        finally
        {
            var mcp = mcp2 ?? mcp1;
            if (mcp is not null)
            {
                try
                {
                    _ = await mcp.CallToolAsync<CloseAppResponse>("close_app", new Dictionary<string, object?>
                    {
                        ["force"] = true,
                        ["timeoutMs"] = 2000
                    });
                }
                catch
                {
                }
            }

            if (app is not null)
            {
                try
                {
                    if (!app.HasExited)
                    {
                        app.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                try
                {
                    app.Dispose();
                }
                catch
                {
                }
            }

            if (mcp1 is not null)
            {
                await mcp1.DisposeAsync();
            }

            if (mcp2 is not null)
            {
                await mcp2.DisposeAsync();
            }
        }
    }

    private static bool ShouldSkipForMissingAssets(InvalidOperationException ex)
    {
        var message = ex.Message;
        return message.Contains("Phase 2 agent payload directory not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Phase 2 Snoop payload directory not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Phase 2 agent assembly not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Snoop injector launcher not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Snoop generic injector not found", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CollectUiaAutomationIds(VisualTreeNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.AutomationId))
        {
            yield return node.AutomationId!;
        }

        foreach (var child in node.Children)
        {
            foreach (var id in CollectUiaAutomationIds(child))
            {
                yield return id;
            }
        }
    }

    private static IEnumerable<string> CollectWpfAutomationIds(WpfVisualTreeNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.AutomationId))
        {
            yield return node.AutomationId!;
        }

        foreach (var child in node.Children)
        {
            foreach (var id in CollectWpfAutomationIds(child))
            {
                yield return id;
            }
        }
    }
}
