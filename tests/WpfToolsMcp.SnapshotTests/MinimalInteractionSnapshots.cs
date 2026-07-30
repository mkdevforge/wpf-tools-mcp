using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class MinimalInteractionSnapshots
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

    private async Task LaunchMinimalAppAsync()
    {
        var exePath = TestAppPaths.FindMinimalTestAppExecutable();
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
    public async Task ClickElement_name_ambiguous_returns_error_snapshot()
    {
        await LaunchMinimalAppAsync();
        try
        {
            InvalidOperationException? ex = null;
            try
            {
                _ = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["locator"] = new Dictionary<string, object?>
                    {
                        ["name"] = "OK"
                    }
                });
                Assert.Fail("Expected click_element to fail due to ambiguous name 'OK'.");
            }
            catch (InvalidOperationException caught)
            {
                ex = caught;
            }

            var message = ex!.Message.Split("--- server stderr", StringSplitOptions.None)[0].TrimEnd();
            await Verifier.Verify(message);
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task ClickElement_name_with_index_updates_click_count_snapshot()
    {
        await LaunchMinimalAppAsync();
        try
        {
            var click = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["name"] = "OK",
                    ["index"] = 0
                }
            });

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["name"] = "Clicks: 1"
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
    public async Task ClickElement_name_strict_false_picks_first_match_snapshot()
    {
        await LaunchMinimalAppAsync();
        try
        {
            var click = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["name"] = "OK",
                    ["strict"] = false
                }
            });

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["nameContains"] = "Clicks:"
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
    public async Task WaitFor_visible_succeeds_snapshot()
    {
        await LaunchMinimalAppAsync();
        try
        {
            var result = await _mcp.CallToolAsync<WaitForResponse>("wait_for", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["nameContains"] = "Clicks:"
                },
                ["state"] = "visible",
                ["timeoutMs"] = 0,
            });

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.True);
                Assert.That(result.BackendUsed, Is.EqualTo(WaitBackend.Wpf));
                Assert.That(result.LastObservedValue?.State, Is.EqualTo(WaitObservedValueState.Value));
                Assert.That(result.LastObservedValue?.Value?.GetValue<bool>(), Is.True);
            });

            await Verifier.Verify(ToStableStructuredWait(result));
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task WaitFor_name_contains_timeout_returns_response_snapshot()
    {
        await LaunchMinimalAppAsync();
        try
        {
            var result = await _mcp.CallToolAsync<WaitForResponse>("wait_for", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["nameContains"] = "Clicks:"
                },
                ["state"] = "name_contains",
                ["expectedText"] = "Clicks: 999",
                ["timeoutMs"] = 0,
                ["throwOnTimeout"] = false,
            });

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.BackendUsed, Is.EqualTo(WaitBackend.Wpf));
                Assert.That(result.ReasonCode, Is.EqualTo("wait_timeout"));
                Assert.That(result.LastObservedValue?.State, Is.EqualTo(WaitObservedValueState.Value));
                Assert.That(result.LastObservedValue?.Value?.GetValue<string>(), Is.EqualTo("Clicks: 0"));
            });

            InvalidOperationException? defaultTimeout = null;
            try
            {
                _ = await _mcp.CallToolAsync<WaitForResponse>("wait_for", new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["locator"] = new Dictionary<string, object?>
                    {
                        ["nameContains"] = "Clicks:"
                    },
                    ["state"] = "name_contains",
                    ["expectedText"] = "Clicks: 999",
                    ["timeoutMs"] = 0
                });
                Assert.Fail("Expected the default timeout to throw.");
            }
            catch (InvalidOperationException ex)
            {
                defaultTimeout = ex;
            }

            Assert.That(defaultTimeout!.Message, Does.Contain("timeout"));

            await Verifier.Verify(ToStableStructuredWait(result));
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task Structured_wait_for_visible_reports_uia_evidence()
    {
        await LaunchMinimalAppAsync();
        try
        {
            var result = await CallStructuredUiaWaitAsync(
                new Dictionary<string, object?>
                {
                    ["kind"] = WaitConditionKind.Visible.ToString()
                },
                timeoutMs: 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.True);
                Assert.That(result.State, Is.EqualTo("visible"));
                Assert.That(result.BackendUsed, Is.EqualTo(WaitBackend.Uia));
                Assert.That(result.LastObservedValue?.Value?.GetValue<bool>(), Is.True);
            });
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task Structured_wait_for_name_timeout_returns_actual_uia_value()
    {
        await LaunchMinimalAppAsync();
        try
        {
            var result = await CallStructuredUiaWaitAsync(
                new Dictionary<string, object?>
                {
                    ["kind"] = WaitConditionKind.NameContains.ToString(),
                    ["comparison"] = WaitComparison.Contains.ToString(),
                    ["expected"] = new Dictionary<string, object?>
                    {
                        ["kind"] = WaitScalarKind.String.ToString(),
                        ["stringValue"] = "Clicks: 999"
                    }
                },
                timeoutMs: 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.BackendUsed, Is.EqualTo(WaitBackend.Uia));
                Assert.That(result.ReasonCode, Is.EqualTo("wait_timeout"));
                Assert.That(result.LastObservedValue?.Value?.GetValue<string>(), Is.EqualTo("Clicks: 0"));
            });
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    private Task<WaitForResponse> CallStructuredUiaWaitAsync(
        Dictionary<string, object?> condition,
        int timeoutMs) =>
        _mcp.CallToolAsync<WaitForResponse>("wait_for", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["backend"] = "uia",
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = "ClickStatus"
            },
            ["condition"] = condition,
            ["timeoutMs"] = timeoutMs
        });

    private static object ToStableStructuredWait(WaitForResponse response) => new
    {
        response.Succeeded,
        response.State,
        ElapsedMs = -1,
        Attempts = -1,
        Observation = response.LastObservation is null
            ? null
            : new
            {
                response.LastObservation.Type,
                response.LastObservation.AutomationId,
                response.LastObservation.Name,
                response.LastObservation.IsEnabled,
                response.LastObservation.IsOffscreen
            },
        response.FailureReason,
        response.BackendUsed,
        response.ReasonCode,
        LastObservedValue = response.LastObservedValue is null
            ? null
            : new
            {
                State = response.LastObservedValue.State.ToString(),
                Value = ToStableWaitValue(response.LastObservedValue.Value),
                response.LastObservedValue.ValueType,
                response.LastObservedValue.Truncated,
                response.LastObservedValue.Detail
            }
    };

    private static object? ToStableWaitValue(JsonNode? value)
    {
        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var text))
            {
                return text;
            }

            if (scalar.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }

            if (scalar.TryGetValue<int>(out var integer))
            {
                return integer;
            }

            if (scalar.TryGetValue<long>(out var longInteger))
            {
                return longInteger;
            }

            if (scalar.TryGetValue<double>(out var number))
            {
                return number;
            }
        }

        if (value is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }

    [Test]
    public async Task SelectItem_listbox_by_text_updates_status_snapshot()
    {
        await LaunchMinimalAppAsync();
        try
        {
            var selected = await _mcp.CallToolAsync<SelectItemResponse>("select_item", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["className"] = "ListBox"
                },
                ["text"] = "Item 10"
            });

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["name"] = "Selected: Item 10"
                }
            });

            await Verifier.Verify(new
            {
                Selected = selected,
                Status = status.Element.Name
            });
        }
        finally
        {
            await CloseAppAsync();
        }
    }
}
