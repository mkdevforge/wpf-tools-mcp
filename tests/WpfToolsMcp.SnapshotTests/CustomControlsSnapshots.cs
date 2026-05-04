using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class CustomControlsSnapshots
{
    private McpTestContext _mcp = null!;
    private string _sessionId = "";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        _mcp = await McpTestContext.StartAsync(serverExe);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_mcp is null)
        {
            return;
        }

        await _mcp.DisposeAsync();
    }

    private async Task LaunchCustomControlsAppAsync()
    {
        var exePath = TestAppPaths.FindCustomControlsTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        var launch = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = workingDirectory,
        });

        _sessionId = launch.SessionId;
    }

    private async Task CloseAppAsync()
    {
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
        finally
        {
            _sessionId = "";
        }
    }

    [Test]
    public async Task Custom_user_control_click_uses_mouse_and_updates_status_snapshot()
    {
        await LaunchCustomControlsAppAsync();
        try
        {
            var click = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Custom_ClickableCard"
                },
                ["clickMode"] = "mouseAlways"
            });

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Custom_Status"
                }
            });

            await Verifier.Verify(new
            {
                Click = click,
                Status = status.Element.Name
            });
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task Invoke_templated_controls_by_wpf_elementId_uses_source_peer_snapshot()
    {
        await LaunchCustomControlsAppAsync();
        try
        {
            var button = await FindSingleWpfElementAsync("Custom_TemplatedButton");
            var toggle = await FindSingleWpfElementAsync("Custom_TemplatedToggleButton");
            var radio = await FindSingleWpfElementAsync("Custom_TemplatedRadioButton");

            var buttonInvoke = await TryInvokeElementAsync(button.ElementId);
            var toggleInvoke = await TryInvokeElementAsync(toggle.ElementId);
            var radioInvoke = await TryInvokeElementAsync(radio.ElementId);

            Assert.That(buttonInvoke.Invoked, Is.True);
            if (toggleInvoke.Error is not null)
            {
                Assert.That(toggleInvoke.Error, Does.Not.Contain("ControlType=Text"));
            }

            if (radioInvoke.Error is not null)
            {
                Assert.That(radioInvoke.Error, Does.Not.Contain("ControlType=Text"));
            }

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Custom_TemplatedStatus"
                }
            });

            await Verifier.Verify(new
            {
                Button = ScrubElementRefForSnapshot(button),
                Toggle = ScrubElementRefForSnapshot(toggle),
                Radio = ScrubElementRefForSnapshot(radio),
                Invokes = new
                {
                    Button = buttonInvoke,
                    Toggle = toggleInvoke,
                    Radio = radioInvoke
                },
                Status = status.Element.Name
            });
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    private sealed record InvokeOutcome(bool Invoked, string? Error);

    private async Task<ElementRef> FindSingleWpfElementAsync(string automationId)
    {
        var matches = await _mcp.CallToolAsync<FindElementsResponse>("find_elements", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["backend"] = "wpf",
            ["query"] = new Dictionary<string, object?>
            {
                ["automationIdEquals"] = automationId
            },
            ["maxResults"] = 3,
            ["returnFields"] = "standard"
        });

        Assert.That(matches.BackendUsed, Is.EqualTo(InspectionBackend.Wpf));
        Assert.That(matches.ReturnedMatches, Is.EqualTo(1));
        Assert.That(matches.Matches[0].ElementId, Does.StartWith("wpf_"));

        return matches.Matches[0];
    }

    private Task<InvokeResponse> InvokeElementAsync(string? elementId)
    {
        Assert.That(elementId, Is.Not.Null.And.Not.Empty);
        return _mcp.CallToolAsync<InvokeResponse>("invoke", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["elementId"] = elementId
        });
    }

    private async Task<InvokeOutcome> TryInvokeElementAsync(string? elementId)
    {
        try
        {
            var response = await InvokeElementAsync(elementId);
            return new InvokeOutcome(response.Invoked, Error: null);
        }
        catch (InvalidOperationException ex)
        {
            return new InvokeOutcome(Invoked: false, Error: NormalizeToolError(ex.Message));
        }
    }

    private static string NormalizeToolError(string message)
    {
        var serverStderrIndex = message.IndexOf("--- server stderr", StringComparison.Ordinal);
        return serverStderrIndex >= 0
            ? message[..serverStderrIndex].TrimEnd()
            : message.TrimEnd();
    }

    private static ElementRef ScrubElementRefForSnapshot(ElementRef element) =>
        element with
        {
            ElementId = "<element>",
            ClassName = string.IsNullOrWhiteSpace(element.ClassName) ? null : "<class>",
            Bounds = element.Bounds is null ? null : new Rect(0, 0, 0, 0)
        };
}
