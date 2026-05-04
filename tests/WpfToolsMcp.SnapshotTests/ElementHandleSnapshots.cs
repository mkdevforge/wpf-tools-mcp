using System.Text.Json.Nodes;
using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class ElementHandleSnapshots
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

    private async Task LaunchAppAsync(string exePath)
    {
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        var launch = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = workingDirectory,
        });

        _sessionId = launch.SessionId;
    }

    private Task LaunchTestAppAsync() => LaunchAppAsync(TestAppPaths.FindTestAppExecutable());

    private Task LaunchDataGridTestAppAsync() => LaunchAppAsync(TestAppPaths.FindDataGridTestAppExecutable());

    private Task LaunchDynamicContentTestAppAsync() => LaunchAppAsync(TestAppPaths.FindDynamicContentTestAppExecutable());

    private async Task CloseTestAppAsync()
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

    private async Task<GetElementPropertiesResponse> WaitForElementAsync(
        Dictionary<string, object?> locator,
        int attempts = 25,
        int delayMs = 75)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                return await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["locator"] = locator
                });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Locator did not match any element", StringComparison.Ordinal))
            {
                await Task.Delay(delayMs);
            }
        }

        Assert.Fail("Element did not appear within timeout.");
        throw new AssertionException("Unreachable.");
    }

    private async Task WaitForElementGoneAsync(
        Dictionary<string, object?> locator,
        int attempts = 25,
        int delayMs = 75)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                _ = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["locator"] = locator
                });

                await Task.Delay(delayMs);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Locator did not match any element", StringComparison.Ordinal))
            {
                return;
            }
        }

        Assert.Fail("Element did not disappear within timeout.");
    }

    private static ElementRef ScrubElementRefForSnapshot(ElementRef element) =>
        element with
        {
            ElementId = "<element>",
            ClassName = string.IsNullOrWhiteSpace(element.ClassName) ? null : "<class>",
            Bounds = element.Bounds is null ? null : new Rect(0, 0, 0, 0)
        };

    [Test]
    public async Task ResolveElement_uia_then_click_by_elementId_snapshot()
    {
        await LaunchTestAppAsync();
        try
        {
            var resolved = await _mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["backend"] = "uia",
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_Button"
                }
            });

            Assert.That(resolved.Element.ElementId, Does.StartWith("uia_"));
            Assert.That(resolved.Element.Bounds, Is.Not.Null, "UIA resolve_element should include Bounds by default.");
            Assert.That(resolved.Element.ClassName, Is.Not.Null.And.Not.Empty, "UIA resolve_element should include ClassName by default.");

            var click = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["elementId"] = resolved.Element.ElementId,
                ["clickMode"] = "mouseAlways"
            });

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_ClickStatus"
                }
            });

            var stable = new
            {
                Resolve = resolved with
                {
                    WindowHandleUsed = 0,
                    Element = ScrubElementRefForSnapshot(resolved.Element)
                },
                Click = click,
                Status = status.Element.Name
            };

            await Verifier.Verify(stable);
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    [Test]
    public async Task ReleaseElement_invalidates_handle_snapshot()
    {
        await LaunchTestAppAsync();
        try
        {
            var resolved = await _mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["backend"] = "uia",
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_Button"
                }
            });

            var released = await _mcp.CallToolAsync<ReleaseElementResponse>("release_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["elementId"] = $" {resolved.Element.ElementId} "
            });

            string? error = null;
            try
            {
                _ = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["elementId"] = resolved.Element.ElementId,
                    ["clickMode"] = "mouseAlways"
                });
            }
            catch (InvalidOperationException ex)
            {
                error = ex.Message.Replace(resolved.Element.ElementId ?? string.Empty, "<element>", StringComparison.Ordinal);
                var tailIndex = error.IndexOf("--- server stderr (tail) ---", StringComparison.Ordinal);
                if (tailIndex >= 0)
                {
                    error = error[..tailIndex].TrimEnd();
                }
            }

            await Verifier.Verify(new
            {
                Release = released,
                Error = error
            });
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    [Test]
    public async Task Drag_slider_by_elementId_updates_value_snapshot()
    {
        await LaunchTestAppAsync();
        try
        {
            var sliderBefore = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_Slider"
                }
            });

            var sliderTree = await _mcp.CallToolAsync<GetVisualTreeResponse>("get_visual_tree", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["backend"] = "uia",
                ["root"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_Slider"
                },
                ["depth"] = 6,
                ["maxNodes"] = 80,
                ["visibleOnly"] = true,
                ["preset"] = "minimal"
            });

            var thumbXPath = FindFirstXPathByType(sliderTree.Root, "Thumb") ?? sliderTree.Root.XPath;

            var thumb = await _mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["backend"] = "uia",
                ["locator"] = new Dictionary<string, object?>
                {
                    ["xpath"] = thumbXPath
                }
            });

            Assert.That(thumb.Element.Bounds, Is.Not.Null, "UIA resolve_element should include Bounds by default.");
            Assert.That(thumb.Element.ClassName, Is.Not.Null.And.Not.Empty, "UIA resolve_element should include ClassName by default.");

            var bounds = sliderBefore.Element.Bounds;
            var toX = bounds.X + bounds.Width - 4;
            var toY = bounds.Y + bounds.Height / 2;

            var drag = await _mcp.CallToolAsync<DragResponse>("drag", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["elementId"] = thumb.Element.ElementId,
                ["toX"] = toX,
                ["toY"] = toY,
                ["steps"] = 18
            });

            var sliderAfter = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_Slider"
                }
            });

            var rangeValue = GetPatternValue(sliderAfter, "RangeValue", "Value")?.GetValue<double>();

            await Verifier.Verify(new
            {
                ResolveThumb = thumb with
                {
                    WindowHandleUsed = 0,
                    Element = ScrubElementRefForSnapshot(thumb.Element)
                },
                Drag = drag,
                Value = rangeValue
            });
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    [Test]
    public async Task GetPathToElement_infers_backend_from_elementId_snapshot()
    {
        await LaunchDataGridTestAppAsync();
        try
        {
            var resolved = await _mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["backend"] = "wpf",
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "DataGrid_PeopleGrid"
                }
            });

            Assert.That(resolved.Element.ElementId, Does.StartWith("wpf_"));

            var path = await _mcp.CallToolAsync<GetPathToElementResponse>("get_path_to_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["elementId"] = resolved.Element.ElementId
            });

            Assert.That(path.BackendUsed, Is.EqualTo(InspectionBackend.Wpf));
            Assert.That(path.XPath, Is.EqualTo(resolved.Element.XPath));

            await Verifier.Verify(new
            {
                Resolve = resolved with
                {
                    WindowHandleUsed = 0,
                    Element = resolved.Element with { ElementId = "<element>" }
                },
                Path = path
            });
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    [Test]
    public async Task Wpf_elementId_that_no_longer_resolves_returns_stale_element_snapshot()
    {
        await LaunchDynamicContentTestAppAsync();
        try
        {
            _ = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Dynamic_AddButton"
                },
                ["clickMode"] = "mouseAlways"
            });

            _ = await WaitForElementAsync(new Dictionary<string, object?>
            {
                ["automationId"] = "Dynamic_NewButton"
            });

            var resolved = await _mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["backend"] = "wpf",
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Dynamic_NewButton"
                }
            });

            Assert.That(resolved.Element.ElementId, Does.StartWith("wpf_"));

            var remove = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Dynamic_RemoveButton"
                },
                ["clickMode"] = "mouseAlways"
            });

            await WaitForElementGoneAsync(new Dictionary<string, object?>
            {
                ["automationId"] = "Dynamic_NewButton"
            });

            string? error = null;
            try
            {
                _ = await _mcp.CallToolAsync<GetDataContextResponse>("get_data_context", new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["elementId"] = resolved.Element.ElementId,
                    ["maxDepth"] = 0
                });
            }
            catch (InvalidOperationException ex)
            {
                error = ex.Message.Replace(resolved.Element.ElementId ?? string.Empty, "<element>", StringComparison.Ordinal);
                var tailIndex = error.IndexOf("--- server stderr (tail) ---", StringComparison.Ordinal);
                if (tailIndex >= 0)
                {
                    error = error[..tailIndex].TrimEnd();
                }
            }

            Assert.That(error, Does.Contain("stale_element: not_found"));

            await Verifier.Verify(new
            {
                Resolve = resolved with
                {
                    WindowHandleUsed = 0,
                    Element = resolved.Element with { ElementId = "<element>" }
                },
                Remove = remove,
                Error = error
            });
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    [Test]
    public async Task ResolveElement_wpf_then_get_data_context_by_elementId_snapshot()
    {
        await LaunchDataGridTestAppAsync();
        try
        {
            var resolved = await _mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["backend"] = "wpf",
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "DataGrid_PeopleGrid"
                }
            });

            Assert.That(resolved.Element.ElementId, Does.StartWith("wpf_"));

            var byElementId = await _mcp.CallToolAsync<GetDataContextResponse>("get_data_context", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["elementId"] = resolved.Element.ElementId,
                ["maxDepth"] = 1,
                ["maxPropertiesPerObject"] = 20,
                ["maxStringLength"] = 200
            });

            var byLocator = await _mcp.CallToolAsync<GetDataContextResponse>("get_data_context", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "DataGrid_PeopleGrid"
                },
                ["maxDepth"] = 1,
                ["maxPropertiesPerObject"] = 20,
                ["maxStringLength"] = 200
            });

            Assert.That(byElementId.DataContextType, Is.Not.Null.And.Not.Empty);
            Assert.That(byLocator.DataContextType, Is.Not.Null.And.Not.Empty);
            Assert.That(byElementId.DataContextType, Is.EqualTo(byLocator.DataContextType));

            var selectedStatusByElementId = byElementId.Data?["SelectedStatus"]?.GetValue<string>();
            var selectedStatusByLocator = byLocator.Data?["SelectedStatus"]?.GetValue<string>();

            await Verifier.Verify(new
            {
                Resolve = resolved with
                {
                    WindowHandleUsed = 0,
                    Element = resolved.Element with { ElementId = "<element>" }
                },
                DataContextTypeByElementId = byElementId.DataContextType,
                DataContextTypeByLocator = byLocator.DataContextType,
                SelectedStatusByElementId = selectedStatusByElementId,
                SelectedStatusByLocator = selectedStatusByLocator
            });
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    [Test]
    public async Task Wpf_elementId_survives_xpath_shift_snapshot()
    {
        await LaunchDynamicContentTestAppAsync();
        try
        {
            var resolved = await _mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["backend"] = "wpf",
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Dynamic_Status"
                }
            });

            Assert.That(resolved.Element.ElementId, Does.StartWith("wpf_"));
            var initialPath = resolved.Element.XPath;

            var insert = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Dynamic_InsertSiblingBeforeStatus"
                },
                ["clickMode"] = "mouseAlways"
            });
            Assert.That(insert.Clicked, Is.True);

            _ = await WaitForElementAsync(new Dictionary<string, object?>
            {
                ["automationId"] = "Dynamic_InsertedSibling"
            });

            var dataContext = await _mcp.CallToolAsync<GetDataContextResponse>("get_data_context", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["elementId"] = resolved.Element.ElementId,
                ["maxDepth"] = 0
            });

            var currentPath = await _mcp.CallToolAsync<GetPathToElementResponse>("get_path_to_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["elementId"] = resolved.Element.ElementId
            });

            Assert.That(currentPath.XPath, Is.Not.EqualTo(initialPath));

            await Verifier.Verify(new
            {
                Resolve = resolved with
                {
                    WindowHandleUsed = 0,
                    Element = resolved.Element with
                    {
                        ElementId = "<element>"
                    }
                },
                InitialPath = initialPath,
                CurrentPath = currentPath.XPath,
                DataContextSucceeded = dataContext is not null
            });
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    private static string? FindFirstXPathByType(TreeNode node, string type)
    {
        if (string.Equals(node.Type, type, StringComparison.OrdinalIgnoreCase))
        {
            return node.XPath;
        }

        foreach (var child in node.Children)
        {
            var found = FindFirstXPathByType(child, type);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }
        }

        return null;
    }

    private static JsonNode? GetPatternValue(GetElementPropertiesResponse response, string patternName, string valueName)
    {
        if (!response.Patterns.TryGetValue(patternName, out var patternNode))
        {
            return null;
        }

        if (patternNode is not JsonObject obj)
        {
            return null;
        }

        if (obj["values"] is not JsonObject values)
        {
            return null;
        }

        return values[valueName];
    }
}
