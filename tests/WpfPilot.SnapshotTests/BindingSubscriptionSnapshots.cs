using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;
using VerifyNUnit;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class BindingSubscriptionSnapshots
{
    private McpTestContext _mcp = null!;
    private string _sessionId = "";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        _mcp = await McpTestContext.StartAsync(serverExe);

        var exePath = TestAppPaths.FindBindingErrorsTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        var launch = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = workingDirectory,
        });

        _sessionId = launch.SessionId;

        try
        {
            _ = await _mcp.CallToolAsync<InjectAgentResponse>("inject_agent", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId
            });
        }
        catch (InvalidOperationException ex) when (ShouldSkipForMissingAssets(ex))
        {
            Assert.Ignore(ex.Message);
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_mcp is null)
        {
            return;
        }

        try
        {
            _ = await _mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["force"] = true,
                ["timeoutMs"] = 2000
            });
        }
        catch
        {
        }

        await _mcp.DisposeAsync();
    }

    [Test]
    public async Task Subscribe_binding_errors_and_poll_snapshot()
    {
        var sub = await _mcp.CallToolAsync<SubscribeBindingErrorsResponse>("subscribe_binding_errors", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["pollIntervalMs"] = 200,
            ["maxQueue"] = 100,
            ["depth"] = 12,
            ["maxNodes"] = 5000
        });

        var poll = await _mcp.CallToolAsync<PollSubscriptionResponse>("poll_subscription", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["subscriptionId"] = sub.SubscriptionId,
            ["timeoutMs"] = 2000,
            ["maxBatch"] = 25
        });

        Assert.That(poll.Events, Is.Not.Empty, "Expected at least one binding error event.");

        var stable = new
        {
            poll.Dropped,
            poll.HasMore,
            Events = poll.Events.Select(e => new
            {
                e.Sequence,
                e.Kind,
                Payload = e.Kind == "binding_error_added"
                    ? ScrubBindingError(e.Payload)
                    : (object?)null
            }).ToArray()
        };

        await Verifier.Verify(stable);

        _ = await _mcp.CallToolAsync<UnsubscribeResponse>("unsubscribe", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["subscriptionId"] = sub.SubscriptionId
        });
    }

    private static object ScrubBindingError(JsonNode payload)
    {
        var error = JsonSerializer.Deserialize<BindingErrorInfo>(payload.ToJsonString())
                    ?? throw new InvalidOperationException("Invalid binding error payload.");
        return new
        {
            error.ElementType,
            error.ElementName,
            error.AutomationId,
            error.TargetProperty,
            error.Path,
            HasErrorMessage = !string.IsNullOrWhiteSpace(error.ErrorMessage),
            error.Status
        };
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
}
