using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class DialogsSnapshots
{
    private const string DialogTitle = "WPF Tools MCP Confirm Dialog";

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

    private async Task LaunchDialogsAppAsync()
    {
        var exePath = TestAppPaths.FindDialogsTestAppExecutable();
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

    private async Task<ListWindowsResponse> ListWindowsAsync() =>
        await _mcp.CallToolAsync<ListWindowsResponse>("list_windows", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId
        });

    private static object ToStableWindows(ListWindowsResponse response) => new
    {
        response.ProcessName,
        Windows = response.Windows
            .Select(w => new
            {
                w.Title,
                Handle = 0,
                Bounds = w.Bounds with { X = 0, Y = 0 },
                w.IsVisible,
                w.IsEnabled
            })
            .OrderBy(w => w.Title, StringComparer.Ordinal)
            .ToArray()
    };

    private async Task<long> WaitForDialogHandleAsync(int attempts = 200, int delayMs = 75)
    {
        for (var i = 0; i < attempts; i++)
        {
            var windows = await ListWindowsAsync();
            var match = windows.Windows.FirstOrDefault(w => string.Equals(w.Title, DialogTitle, StringComparison.Ordinal));
            if (match is not null)
            {
                return match.Handle;
            }

            await Task.Delay(delayMs);
        }

        Assert.Fail($"Dialog window '{DialogTitle}' did not appear within timeout.");
        throw new AssertionException("Unreachable.");
    }

    private async Task WaitForDialogClosedAsync(int attempts = 200, int delayMs = 75)
    {
        for (var i = 0; i < attempts; i++)
        {
            var windows = await ListWindowsAsync();
            var any = windows.Windows.Any(w => string.Equals(w.Title, DialogTitle, StringComparison.Ordinal));
            if (!any)
            {
                return;
            }

            await Task.Delay(delayMs);
        }

        Assert.Fail($"Dialog window '{DialogTitle}' did not close within timeout.");
    }

    [Test]
    public async Task Modal_dialog_can_be_focused_and_clicked_by_window_handle_snapshot()
    {
        await LaunchDialogsAppAsync();
        try
        {
            var openDialog = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Dialogs_OpenDialog"
                },
                ["clickMode"] = "mouseAlways"
            });

            var dialogHandle = await WaitForDialogHandleAsync();
            var windowsWhileDialogOpen = await ListWindowsAsync();

            var focus = await _mcp.CallToolAsync<FocusWindowResponse>("set_active_window", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["title"] = DialogTitle
            });

            var clickOk = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["windowHandle"] = dialogHandle,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Dialog_OK"
                }
            });

            await WaitForDialogClosedAsync();

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Dialogs_Status"
                }
            });

            var windowsAfterClose = await ListWindowsAsync();

            await Verifier.Verify(new
            {
                OpenDialog = openDialog,
                WindowsWhileDialogOpen = ToStableWindows(windowsWhileDialogOpen),
                Focus = focus with { Handle = 0 },
                ClickOk = clickOk,
                Status = status.Element.Name,
                WindowsAfterClose = ToStableWindows(windowsAfterClose)
            });
        }
        finally
        {
            await CloseAppAsync();
        }
    }
}
