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
        string sessionId1 = "";
        string sessionId2 = "";

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

            var attached1 = await mcp1.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["pid"] = app.Id
            });
            sessionId1 = attached1.SessionId;

            InjectAgentResponse inject1;
            try
            {
                inject1 = await mcp1.CallToolAsync<InjectAgentResponse>("inject_agent", new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId1
                });
            }
            catch (InvalidOperationException ex) when (ShouldSkipForMissingAssets(ex))
            {
                Assert.Ignore(ex.Message);
                return;
            }

            var pong1 = await mcp1.CallToolAsync<AgentPingResponse>("agent_ping", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId1
            });
            Assert.That(pong1.Message, Is.EqualTo("pong").IgnoreCase);

            var uiaTree1 = await mcp1.CallToolAsync<GetVisualTreeResponse>("get_visual_tree", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId1,
                ["backend"] = "uia",
                ["depth"] = 10,
                ["maxNodes"] = 2000
            });

            var wpfTree1 = await mcp1.CallToolAsync<GetVisualTreeResponse>("get_visual_tree", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId1,
                ["backend"] = "wpf",
                ["depth"] = 12,
                ["maxNodes"] = 2000
            });

            // Simulate MCP server restart (new process, new AutomationController instance).
            await mcp1.DisposeAsync();
            mcp1 = null;

            mcp2 = await McpTestContext.StartAsync(serverExe);
            var attached2 = await mcp2.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["pid"] = app.Id
            });
            sessionId2 = attached2.SessionId;

            var inject2 = await mcp2.CallToolAsync<InjectAgentResponse>("inject_agent", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId2
            });
            Assert.That(inject2.Injected, Is.False, "Expected reconnect without reinjection after MCP restart.");
            Assert.That(inject2.PipeName, Is.EqualTo(inject1.PipeName));

            var pong2 = await mcp2.CallToolAsync<AgentPingResponse>("agent_ping", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId2
            });
            Assert.That(pong2.Message, Is.EqualTo("pong").IgnoreCase);

            var wpfTree2 = await mcp2.CallToolAsync<GetVisualTreeResponse>("get_visual_tree", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId2,
                ["backend"] = "wpf",
                ["depth"] = 12,
                ["maxNodes"] = 2000
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
                UiaAutomationIds = CollectAutomationIds(uiaTree1.Root).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                WpfAutomationIds1 = CollectAutomationIds(wpfTree1.Root).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                WpfAutomationIds2 = CollectAutomationIds(wpfTree2.Root).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
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
                    var sid = mcp2 is not null ? sessionId2 : sessionId1;
                    if (!string.IsNullOrWhiteSpace(sid))
                    {
                        _ = await mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
                        {
                            ["sessionId"] = sid,
                            ["force"] = true,
                            ["timeoutMs"] = 2000
                        });
                    }
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

    private static IEnumerable<string> CollectAutomationIds(TreeNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.AutomationId))
        {
            yield return node.AutomationId!;
        }

        foreach (var child in node.Children)
        {
            foreach (var id in CollectAutomationIds(child))
            {
                yield return id;
            }
        }
    }
}
