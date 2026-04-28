using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This smoke runner only supports Windows.");
    return 1;
}

var opts = Options.Parse(args);
if (opts.ShowHelp)
{
    Options.PrintHelp();
    return 0;
}

Directory.CreateDirectory(opts.OutDir);
Directory.CreateDirectory(Path.Combine(opts.OutDir, "actions"));
Directory.CreateDirectory(Path.Combine(opts.OutDir, "trees"));

using var runCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    runCts.Cancel();
};

var stderrLines = new ConcurrentQueue<string>();

var report = new JsonObject
{
    ["startedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
    ["serverExe"] = opts.ServerExe,
    ["targetExe"] = opts.TargetExe,
    ["workDir"] = opts.WorkDir,
    ["outDir"] = opts.OutDir,
    ["depth"] = opts.Depth,
    ["maxActions"] = opts.MaxActions,
    ["maxNodes"] = opts.MaxNodes,
    ["timeoutMs"] = opts.TimeoutMs,
    ["includePhase2"] = opts.IncludePhase2,
    ["steps"] = new JsonArray()
};

var screenshotsIndex = 0;
var actionCount = 0;

try
{
    await using var mcp = await McpWrapper.StartAsync(opts.ServerExe, stderrLines, runCts.Token);

    var launch = await CallStepAsync(
        report,
        "launch_app",
        mcp,
        "launch_app",
        new JsonObject
        {
            ["exePath"] = opts.TargetExe,
            ["workingDirectory"] = opts.WorkDir
        },
        opts,
        runCts.Token);

    var sessionId = (launch["SessionId"] ?? launch["sessionId"])?.GetValue<string>() ?? "";
    report["sessionId"] = sessionId;

    report["pid"] = (launch["Pid"] ?? launch["pid"])?.GetValue<int>();
    report["processName"] = (launch["ProcessName"] ?? launch["processName"])?.GetValue<string>();

    var windows = await WaitForWindowsAsync(report, mcp, opts, sessionId, runCts.Token);
    var main = PickMainWindow(windows);
    report["mainWindowHandle"] = main.Handle;
    report["mainWindowTitle"] = main.Title;

    await CallStepAsync(
        report,
        "set_active_window",
        mcp,
        "set_active_window",
        new JsonObject { ["sessionId"] = sessionId, ["windowHandle"] = main.Handle },
        opts,
        runCts.Token);

    await CaptureAsync(report, mcp, opts, sessionId, main.Handle, "00-baseline-auto.png", "auto", runCts.Token);
    await CaptureAsync(report, mcp, opts, sessionId, main.Handle, "00-baseline-printWindow.png", "printWindow", runCts.Token);
    await CaptureAsync(report, mcp, opts, sessionId, main.Handle, "00-baseline-screen.png", "screen", runCts.Token);

    if (opts.IncludePhase2)
    {
        await TryPhase2Async(report, mcp, opts, sessionId, main.Handle, runCts.Token);
    }

    var uiaTree = await CallStepAsync(
        report,
        "get_visual_tree",
        mcp,
        "get_visual_tree",
        new JsonObject
        {
            ["sessionId"] = sessionId,
            ["backend"] = "uia",
            ["windowHandle"] = main.Handle,
            ["depth"] = opts.Depth,
            ["maxNodes"] = opts.MaxNodes,
            ["fields"] = new JsonArray { "isEnabled", "isOffscreen" }
        },
        opts,
        runCts.Token);

    await File.WriteAllTextAsync(
        Path.Combine(opts.OutDir, "trees", "uia.json"),
        uiaTree.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
        runCts.Token);

    var root = uiaTree["Root"] as JsonObject ?? uiaTree["root"] as JsonObject;
    if (root is null)
    {
        throw new InvalidOperationException("get_visual_tree returned no Root.");
    }

    var nodes = new List<UiaNode>();
    CollectNodes(root, nodes);

    var actionLimits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["scroll_to_element"] = 10,
        ["invoke"] = 25,
        ["click_element"] = 25,
        ["type_text"] = 10,
        ["set_value"] = 10,
        ["select_item"] = 10
    };

    var actionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var inspected = 0;

    bool CanDo(string name)
    {
        return actionCount < opts.MaxActions &&
            actionCounts.GetValueOrDefault(name) < actionLimits.GetValueOrDefault(name, 1);
    }

    void Count(string name)
    {
        actionCounts[name] = actionCounts.GetValueOrDefault(name) + 1;
        actionCount++;
    }

    foreach (var node in nodes)
    {
        runCts.Token.ThrowIfCancellationRequested();
        if (inspected++ >= opts.MaxNodes)
        {
            break;
        }

        if (actionCount >= opts.MaxActions)
        {
            break;
        }

        if (!node.IsEnabled || string.IsNullOrWhiteSpace(node.XPath))
        {
            continue;
        }

        if (node.XPath.StartsWith("/Window/TitleBar", StringComparison.Ordinal))
        {
            continue;
        }

        if (node.IsOffscreen && CanDo("scroll_to_element"))
        {
            var scrolled = await SafeToolAsync(
                report,
                $"scroll_to_element:{node.XPath}",
                mcp,
                "scroll_to_element",
                new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["locator"] = new JsonObject { ["xpath"] = node.XPath },
                    ["windowHandle"] = main.Handle
                },
                opts,
                runCts.Token);

            if (scrolled)
            {
                Count("scroll_to_element");
                if (await CaptureAsync(report, mcp, opts, sessionId, main.Handle, $"actions/{screenshotsIndex:D3}-after-scroll.png", "auto", runCts.Token))
                {
                    screenshotsIndex++;
                }
            }
        }

        if (LooksDangerousToClick(node.Name))
        {
            continue;
        }

        JsonObject? patterns = null;
        try
        {
            var props = await CallStepAsync(
                report,
                $"get_element_properties:{node.XPath}",
                mcp,
                "get_element_properties",
                new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["locator"] = new JsonObject { ["xpath"] = node.XPath },
                    ["windowHandle"] = main.Handle
                },
                opts,
                runCts.Token);

            patterns = props["Patterns"] as JsonObject ?? props["patterns"] as JsonObject;
        }
        catch
        {
            continue;
        }

        if (patterns is null)
        {
            continue;
        }

        if (CanDo("invoke") && patterns.ContainsKey("Invoke"))
        {
            var ok = await SafeToolAsync(
                report,
                $"invoke:{node.XPath}",
                mcp,
                "invoke",
                new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["locator"] = new JsonObject { ["xpath"] = node.XPath },
                    ["windowHandle"] = main.Handle
                },
                opts,
                runCts.Token);

            if (ok)
            {
                Count("invoke");
                if (await CaptureAsync(report, mcp, opts, sessionId, main.Handle, $"actions/{screenshotsIndex:D3}-after-invoke.png", "auto", runCts.Token))
                {
                    screenshotsIndex++;
                }
                continue;
            }
        }

        if (CanDo("type_text") &&
            string.Equals(node.ElementType, "Edit", StringComparison.OrdinalIgnoreCase) &&
            patterns.ContainsKey("Value"))
        {
            var ok = await SafeToolAsync(
                report,
                $"type_text:{node.XPath}",
                mcp,
                "type_text",
                new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["locator"] = new JsonObject { ["xpath"] = node.XPath },
                    ["text"] = $"WpfPilotSmoke {DateTimeOffset.Now:HHmmss}",
                    ["windowHandle"] = main.Handle
                },
                opts,
                runCts.Token);

            if (ok)
            {
                Count("type_text");
                if (await CaptureAsync(report, mcp, opts, sessionId, main.Handle, $"actions/{screenshotsIndex:D3}-after-type.png", "auto", runCts.Token))
                {
                    screenshotsIndex++;
                }
                continue;
            }
        }

        if (CanDo("set_value") && patterns.ContainsKey("RangeValue"))
        {
            var mid = TryComputeMidRange(patterns["RangeValue"] as JsonObject) ?? 0.5d;
            var ok = await SafeToolAsync(
                report,
                $"set_value:{node.XPath}",
                mcp,
                "set_value",
                new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["locator"] = new JsonObject { ["xpath"] = node.XPath },
                    ["value"] = mid,
                    ["windowHandle"] = main.Handle
                },
                opts,
                runCts.Token);

            if (ok)
            {
                Count("set_value");
                if (await CaptureAsync(report, mcp, opts, sessionId, main.Handle, $"actions/{screenshotsIndex:D3}-after-set-value.png", "auto", runCts.Token))
                {
                    screenshotsIndex++;
                }
                continue;
            }
        }

        if (CanDo("select_item") &&
            (string.Equals(node.ElementType, "ComboBox", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.ElementType, "Tab", StringComparison.OrdinalIgnoreCase) ||
             patterns.ContainsKey("Selection")))
        {
            var ok = await TrySelectAsync(report, mcp, opts, sessionId, main.Handle, node.XPath, index: 1, runCts.Token) ||
                     await TrySelectAsync(report, mcp, opts, sessionId, main.Handle, node.XPath, index: 0, runCts.Token);

            if (ok)
            {
                Count("select_item");
                if (await CaptureAsync(report, mcp, opts, sessionId, main.Handle, $"actions/{screenshotsIndex:D3}-after-select.png", "auto", runCts.Token))
                {
                    screenshotsIndex++;
                }
                continue;
            }
        }

        if (CanDo("click_element") &&
            (string.Equals(node.ElementType, "Button", StringComparison.OrdinalIgnoreCase) ||
             patterns.ContainsKey("Toggle") ||
             patterns.ContainsKey("Invoke")))
        {
            var ok = await SafeToolAsync(
                report,
                $"click_element:{node.XPath}",
                mcp,
                "click_element",
                new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["locator"] = new JsonObject { ["xpath"] = node.XPath },
                    ["windowHandle"] = main.Handle,
                    ["clickMode"] = "auto"
                },
                opts,
                runCts.Token);

            if (ok)
            {
                Count("click_element");
                if (await CaptureAsync(report, mcp, opts, sessionId, main.Handle, $"actions/{screenshotsIndex:D3}-after-click.png", "auto", runCts.Token))
                {
                    screenshotsIndex++;
                }
            }
        }
    }

    report["actionsAttempted"] = actionCount;
    report["actionCounts"] = JsonSerializer.SerializeToNode(actionCounts, new JsonSerializerOptions { WriteIndented = true });

    await SafeToolAsync(
        report,
        "close_session",
        mcp,
        "close_session",
        new JsonObject
        {
            ["sessionId"] = sessionId,
            ["force"] = true,
            ["timeoutMs"] = 5000
        },
        opts,
        runCts.Token);
}
catch (OperationCanceledException)
{
    report["aborted"] = true;
}
catch (Exception ex)
{
    report["fatalError"] = ex.ToString();
}
finally
{
    report["finishedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");
    await File.WriteAllTextAsync(
        Path.Combine(opts.OutDir, "server-stderr.log"),
        string.Join(Environment.NewLine, stderrLines.ToArray()),
        CancellationToken.None);

    await File.WriteAllTextAsync(
        Path.Combine(opts.OutDir, "report.json"),
        report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
        CancellationToken.None);
}

Console.WriteLine($"Smoke run complete. Output: {opts.OutDir}");
return report["fatalError"] is null ? 0 : 1;

static async Task<JsonObject> CallStepAsync(
    JsonObject report,
    string name,
    McpWrapper mcp,
    string toolName,
    JsonObject args,
    OptionsModel opts,
    CancellationToken cancellationToken)
{
    var steps = (JsonArray)report["steps"]!;

    var step = new JsonObject
    {
        ["name"] = name,
        ["tool"] = toolName,
        ["args"] = args
    };
    steps.Add(step);

    var sw = Stopwatch.StartNew();
    try
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(opts.TimeoutMs));

        var result = await mcp.CallToolJsonAsync(toolName, args, timeoutCts.Token);
        step["success"] = true;
        step["durationMs"] = (int)sw.ElapsedMilliseconds;

        return result as JsonObject ?? new JsonObject { ["value"] = result };
    }
    catch (Exception ex)
    {
        step["success"] = false;
        step["durationMs"] = (int)sw.ElapsedMilliseconds;
        step["error"] = ex.Message;
        throw;
    }
}

static async Task<bool> SafeToolAsync(
    JsonObject report,
    string name,
    McpWrapper mcp,
    string toolName,
    JsonObject args,
    OptionsModel opts,
    CancellationToken cancellationToken)
{
    try
    {
        await CallStepAsync(report, name, mcp, toolName, args, opts, cancellationToken);
        return true;
    }
    catch
    {
        return false;
    }
}

static async Task<bool> CaptureAsync(
    JsonObject report,
    McpWrapper mcp,
    OptionsModel opts,
    string sessionId,
    long windowHandle,
    string relativePath,
    string captureMode,
    CancellationToken cancellationToken)
{
    var requestedPath = Path.Combine(opts.OutDir, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(requestedPath)!);

    JsonObject screenshot;
    try
    {
        screenshot = await CallStepAsync(
            report,
            $"take_screenshot:{captureMode}",
            mcp,
            "take_screenshot",
            new JsonObject
            {
                ["sessionId"] = sessionId,
                ["windowHandle"] = windowHandle,
                ["captureMode"] = captureMode,
                ["outputPath"] = requestedPath
            },
            opts,
            cancellationToken);
    }
    catch
    {
        return false;
    }

    var savedPath = (screenshot["Path"] ?? screenshot["path"])?.GetValue<string>() ?? requestedPath;
    return File.Exists(savedPath);
}

static async Task<bool> TrySelectAsync(
    JsonObject report,
    McpWrapper mcp,
    OptionsModel opts,
    string sessionId,
    long windowHandle,
    string containerXPath,
    int index,
    CancellationToken cancellationToken)
{
    try
    {
        await CallStepAsync(
            report,
            $"select_item:{containerXPath}:{index}",
            mcp,
            "select_item",
            new JsonObject
            {
                ["sessionId"] = sessionId,
                ["locator"] = new JsonObject { ["xpath"] = containerXPath },
                ["index"] = index,
                ["windowHandle"] = windowHandle
            },
            opts,
            cancellationToken);
        return true;
    }
    catch
    {
        return false;
    }
}

static bool LooksDangerousToClick(string? name)
{
    if (string.IsNullOrWhiteSpace(name))
    {
        return false;
    }

    return name.Contains("close", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("exit", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("quit", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("minimize", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("maximize", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("restore", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("system", StringComparison.OrdinalIgnoreCase);
}

static double? TryComputeMidRange(JsonObject? rangeValuePattern)
{
    var values = rangeValuePattern?["values"] as JsonObject;
    if (values is null)
    {
        return null;
    }

    if (!TryGetDouble(values, "Minimum", out var min) || !TryGetDouble(values, "Maximum", out var max))
    {
        return null;
    }

    if (double.IsNaN(min) || double.IsNaN(max) || double.IsInfinity(min) || double.IsInfinity(max))
    {
        return null;
    }

    if (max <= min)
    {
        return min;
    }

    return min + ((max - min) / 2.0d);
}

static bool TryGetDouble(JsonObject obj, string name, out double value)
{
    value = default;

    if (!obj.TryGetPropertyValue(name, out var node) || node is null)
    {
        return false;
    }

    try
    {
        value = node.GetValue<double>();
        return true;
    }
    catch
    {
        try
        {
            var s = node.GetValue<string>();
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }
        catch
        {
            return false;
        }
    }
}

static async Task TryPhase2Async(
    JsonObject report,
    McpWrapper mcp,
    OptionsModel opts,
    string sessionId,
    long windowHandle,
    CancellationToken cancellationToken)
{
    JsonObject injected;
    try
    {
        injected = await CallStepAsync(
            report,
            "inject_agent",
            mcp,
            "inject_agent",
            new JsonObject { ["sessionId"] = sessionId },
            opts,
            cancellationToken);
    }
    catch (Exception ex)
    {
        report["phase2"] = new JsonObject { ["injected"] = false, ["error"] = ex.Message };
        return;
    }

    var phase2 = new JsonObject
    {
        ["injected"] = (injected["Injected"] ?? injected["injected"])?.GetValue<bool>() ?? false,
        ["pipeName"] = (injected["PipeName"] ?? injected["pipeName"])?.GetValue<string>()
    };
    report["phase2"] = phase2;

    await SafeToolAsync(report, "agent_ping", mcp, "agent_ping", new JsonObject { ["sessionId"] = sessionId }, opts, cancellationToken);

    try
    {
        var wpfTree = await CallStepAsync(
            report,
            "get_visual_tree_wpf",
            mcp,
            "get_visual_tree",
            new JsonObject
            {
                ["sessionId"] = sessionId,
                ["backend"] = "wpf",
                ["windowHandle"] = windowHandle,
                ["depth"] = Math.Min(opts.Depth, 6)
            },
            opts,
            cancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(opts.OutDir, "trees", "wpf.json"),
            wpfTree.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }
    catch (Exception ex)
    {
        phase2["error"] = ex.Message;
    }
}

static async Task<List<WindowInfo>> WaitForWindowsAsync(
    JsonObject report,
    McpWrapper mcp,
    OptionsModel opts,
    string sessionId,
    CancellationToken cancellationToken)
{
    var stopAt = DateTimeOffset.UtcNow.AddSeconds(20);
    Exception? lastError = null;

    while (DateTimeOffset.UtcNow < stopAt)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await CallStepAsync(
                report,
                "list_windows",
                mcp,
                "list_windows",
                new JsonObject { ["sessionId"] = sessionId },
                opts,
                cancellationToken);

            var arr = result["Windows"] as JsonArray ?? result["windows"] as JsonArray;
            if (arr is null || arr.Count == 0)
            {
                await Task.Delay(250, cancellationToken);
                continue;
            }

            var windows = new List<WindowInfo>();
            foreach (var item in arr.OfType<JsonObject>())
            {
                if (TryReadWindow(item, out var w))
                {
                    windows.Add(w);
                }
            }

            if (windows.Count > 0)
            {
                return windows;
            }
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        await Task.Delay(250, cancellationToken);
    }

    throw new InvalidOperationException("Timed out waiting for list_windows.", lastError);
}

static WindowInfo PickMainWindow(IReadOnlyList<WindowInfo> windows)
{
    return windows
        .Where(w => w.IsVisible && w.Bounds.Width > 0 && w.Bounds.Height > 0)
        .OrderByDescending(w => w.Bounds.Width * w.Bounds.Height)
        .First();
}

static bool TryReadWindow(JsonObject node, out WindowInfo window)
{
    window = default!;

    var title = (node["Title"] ?? node["title"])?.GetValue<string>() ?? string.Empty;
    var handle = (node["Handle"] ?? node["handle"])?.GetValue<long>() ?? 0L;
    var bounds = node["Bounds"] as JsonObject ?? node["bounds"] as JsonObject;

    if (handle == 0L || bounds is null)
    {
        return false;
    }

    var rect = new RectInfo(
        X: (bounds["X"] ?? bounds["x"])?.GetValue<int>() ?? 0,
        Y: (bounds["Y"] ?? bounds["y"])?.GetValue<int>() ?? 0,
        Width: (bounds["Width"] ?? bounds["width"])?.GetValue<int>() ?? 0,
        Height: (bounds["Height"] ?? bounds["height"])?.GetValue<int>() ?? 0);

    var isVisible = (node["IsVisible"] ?? node["isVisible"])?.GetValue<bool>() ?? true;
    var isEnabled = (node["IsEnabled"] ?? node["isEnabled"])?.GetValue<bool>() ?? true;

    window = new WindowInfo(title, handle, rect, isVisible, isEnabled);
    return true;
}

static void CollectNodes(JsonObject node, List<UiaNode> destination)
{
    var elementType = (node["Type"] ?? node["type"] ?? node["ElementType"] ?? node["elementType"])?.GetValue<string>() ?? string.Empty;
    var name = (node["Name"] ?? node["name"])?.GetValue<string>();
    var xpath = (node["XPath"] ?? node["xPath"] ?? node["xpath"])?.GetValue<string>() ?? string.Empty;
    var isEnabled = (node["IsEnabled"] ?? node["isEnabled"])?.GetValue<bool>() ?? true;
    var isOffscreen = (node["IsOffscreen"] ?? node["isOffscreen"])?.GetValue<bool>() ?? false;

    if (!string.IsNullOrWhiteSpace(xpath))
    {
        destination.Add(new UiaNode(elementType, name, xpath, isEnabled, isOffscreen));
    }

    if ((node["Children"] ?? node["children"]) is JsonArray children)
    {
        foreach (var child in children.OfType<JsonObject>())
        {
            CollectNodes(child, destination);
        }
    }
}

static class Options
{
    public static OptionsModel Parse(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            return new OptionsModel(true, string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, 0, 0, false);
        }

        string? server = null;
        string? exe = null;
        string? workDir = null;
        string? outDir = null;
        var depth = 6;
        var maxActions = 200;
        var maxNodes = 800;
        var timeoutMs = 15000;
        var includePhase2 = true;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[i + 1];
                i++;
            }

            switch (key)
            {
                case "server":
                    server = value;
                    break;
                case "exe":
                    exe = value;
                    break;
                case "workdir":
                    workDir = value;
                    break;
                case "out":
                    outDir = value;
                    break;
                case "depth":
                    if (int.TryParse(value, out var parsedDepth))
                    {
                        depth = Math.Clamp(parsedDepth, 1, 20);
                    }
                    break;
                case "max-actions":
                    if (int.TryParse(value, out var parsedMax))
                    {
                        maxActions = Math.Clamp(parsedMax, 1, 2000);
                    }
                    break;
                case "max-nodes":
                    if (int.TryParse(value, out var parsedNodes))
                    {
                        maxNodes = Math.Clamp(parsedNodes, 10, 10000);
                    }
                    break;
                case "timeout-ms":
                    if (int.TryParse(value, out var parsedTimeout))
                    {
                        timeoutMs = Math.Clamp(parsedTimeout, 1000, 120000);
                    }
                    break;
                case "include-phase2":
                    if (bool.TryParse(value, out var parsedBool))
                    {
                        includePhase2 = parsedBool;
                    }
                    break;
            }
        }

        exe ??= @"C:\Users\mikae\Development\MkUi\samples\MkUi.Demo\bin\Release\net10.0-windows\MkUi.Demo.exe";
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException($"Target exe not found: '{exe}'. Use --exe.");
        }

        server ??= FindLatestServerExe();
        if (!File.Exists(server))
        {
            throw new FileNotFoundException($"Server exe not found: '{server}'. Use --server.");
        }

        workDir ??= Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory;
        outDir ??= Path.Combine(FindRepoRootOrCwd(), "artifacts", "smoke", DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));

        return new OptionsModel(false, server, exe, workDir, outDir, depth, maxActions, maxNodes, timeoutMs, includePhase2);
    }

    public static void PrintHelp()
    {
        Console.WriteLine(
            """
WpfPilot MCP smoke runner (black-box)

Usage:
  dotnet run --project tools/WpfPilot.McpSmokeRunner -- [options]

Options:
  --server <path>         Path to WpfPilot.McpServer.exe (auto-discovered if omitted)
  --exe <path>            Path to target WPF exe (defaults to MkUi.Demo path on this machine)
  --workdir <path>        Working directory for the target app (defaults to exe folder)
  --out <path>            Output directory (default: artifacts/smoke/<timestamp>)
  --depth <n>             UIA tree depth (default: 6)
  --max-actions <n>       Max interactions attempted (default: 200)
  --max-nodes <n>         Max UIA nodes to inspect (default: 800)
  --timeout-ms <n>        Per-tool timeout in ms (default: 15000)
  --include-phase2 <bool> Attempt inject_agent + get_visual_tree (backend=wpf) (default: true)
  --help                  Show help
""");
    }

    private static string FindLatestServerExe()
    {
        var root = FindRepoRootOrCwd();
        var binRoot = Path.Combine(root, "src", "WpfPilot.McpServer", "bin");
        if (!Directory.Exists(binRoot))
        {
            throw new DirectoryNotFoundException($"Could not find '{binRoot}'. Build WpfPilot.McpServer first or specify --server.");
        }

        var candidates = Directory.EnumerateFiles(binRoot, "WpfPilot.McpServer.exe", SearchOption.AllDirectories)
            .Where(p => p.Contains("net8.0-windows", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new FileNotFoundException($"Could not find WpfPilot.McpServer.exe under '{binRoot}'.");
        }

        return candidates
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
    }

    private static string FindRepoRootOrCwd()
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WpfPilot.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Environment.CurrentDirectory;
    }
}

sealed record OptionsModel(
    bool ShowHelp,
    string ServerExe,
    string TargetExe,
    string WorkDir,
    string OutDir,
    int Depth,
    int MaxActions,
    int MaxNodes,
    int TimeoutMs,
    bool IncludePhase2);

sealed class McpWrapper : IAsyncDisposable
{
    private readonly McpClient _client;
    private readonly ConcurrentQueue<string> _stderrLines;

    private McpWrapper(McpClient client, ConcurrentQueue<string> stderrLines)
    {
        _client = client;
        _stderrLines = stderrLines;
    }

    public static async Task<McpWrapper> StartAsync(string serverExePath, ConcurrentQueue<string> stderrLines, CancellationToken cancellationToken)
    {
        var transportOptions = new StdioClientTransportOptions
        {
            Command = serverExePath,
            Arguments = ["--tool-profile", "diagnostics"],
            Name = "WpfPilot.McpSmokeRunner",
            StandardErrorLines = line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    stderrLines.Enqueue(line);
                }
            }
        };

        var transport = new StdioClientTransport(transportOptions, NullLoggerFactory.Instance);
        var client = await McpClient.CreateAsync(transport, clientOptions: null, NullLoggerFactory.Instance, cancellationToken);
        return new McpWrapper(client, stderrLines);
    }

    public async Task<JsonNode> CallToolJsonAsync(string toolName, JsonObject args, CancellationToken cancellationToken)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kvp in args)
        {
            if (kvp.Value is null)
            {
                continue;
            }

            dict[kvp.Key] = kvp.Value is JsonValue value
                ? value.GetValue<object?>()
                : kvp.Value;
        }

        var result = await _client.CallToolAsync(toolName, dict, progress: null, options: null, cancellationToken);
        if (result.IsError is true)
        {
            var details = ExtractText(result);
            var stderrTail = GetStderrTail(maxLines: 30);
            if (stderrTail.Count > 0)
            {
                details += $"{Environment.NewLine}--- server stderr (tail) ---{Environment.NewLine}{string.Join(Environment.NewLine, stderrTail)}";
            }

            throw new InvalidOperationException($"Tool '{toolName}' failed: {details}");
        }

        var json = ExtractJson(result);
        try
        {
            return JsonNode.Parse(json) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject { ["text"] = json };
        }
    }

    private IReadOnlyList<string> GetStderrTail(int maxLines)
    {
        var lines = _stderrLines.ToArray();
        if (lines.Length == 0)
        {
            return Array.Empty<string>();
        }

        var take = Math.Min(maxLines, lines.Length);
        return lines[^take..];
    }

    private static string ExtractJson(CallToolResult result)
    {
        foreach (var content in result.Content)
        {
            if (content is TextContentBlock text)
            {
                return text.Text;
            }
        }

        if (result.StructuredContent is not null)
        {
            return result.StructuredContent.ToJsonString();
        }

        throw new InvalidOperationException("Tool returned no text or structured content.");
    }

    private static string ExtractText(CallToolResult result)
    {
        foreach (var content in result.Content)
        {
            if (content is TextContentBlock text)
            {
                return text.Text;
            }
        }

        throw new InvalidOperationException("Tool returned no text content.");
    }

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();
}

sealed record WindowInfo(string Title, long Handle, RectInfo Bounds, bool IsVisible, bool IsEnabled);

sealed record RectInfo(int X, int Y, int Width, int Height);

sealed record UiaNode(string ElementType, string? Name, string XPath, bool IsEnabled, bool IsOffscreen);
