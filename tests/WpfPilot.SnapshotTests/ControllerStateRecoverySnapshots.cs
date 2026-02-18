using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class ControllerStateRecoverySnapshots
{
    private McpTestContext _mcp = null!;

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

    [Test]
    public async Task Attach_failure_does_not_block_followup_launch_snapshot()
    {
        var attachFailure = await CaptureAttachFailureToCurrentProcessAsync();

        var launch = await LaunchTestAppAsync();
        try
        {
            var windows = await _mcp.CallToolAsync<ListWindowsResponse>("list_windows");
            var stableWindows = windows.Windows
                .Select(w => w with { Handle = 0, Bounds = w.Bounds with { X = 0, Y = 0 } })
                .ToArray();

            await Verifier.Verify(new
            {
                AttachFailure = attachFailure,
                Launch = launch with { Pid = -1 },
                Windows = stableWindows
            });
        }
        finally
        {
            await CloseAppIfAttachedAsync();
        }
    }

    [Test]
    public async Task ResetState_allows_relaunch_without_close_snapshot()
    {
        var firstLaunch = await LaunchTestAppAsync();
        try
        {
            var reset = await _mcp.CallToolAsync<ResetStateResponse>("reset_state");
            var firstProcessAliveAfterReset = IsProcessAlive(firstLaunch.Pid);

            var secondLaunch = await LaunchTestAppAsync();
            try
            {
                var windows = await _mcp.CallToolAsync<ListWindowsResponse>("list_windows");
                var stableWindows = windows.Windows
                    .Select(w => w with { Handle = 0, Bounds = w.Bounds with { X = 0, Y = 0 } })
                    .ToArray();

                await Verifier.Verify(new
                {
                    FirstLaunch = firstLaunch with { Pid = -1 },
                    Reset = reset,
                    FirstProcessAliveAfterReset = firstProcessAliveAfterReset,
                    SecondLaunch = secondLaunch with { Pid = -1 },
                    Windows = stableWindows
                });
            }
            finally
            {
                await CloseAppIfAttachedAsync();
            }
        }
        finally
        {
            KillProcessIfRunning(firstLaunch.Pid);
        }
    }

    [Test]
    public async Task Attach_by_process_name_accepts_dotted_name_with_or_without_exe_suffix()
    {
        var withoutExeSuffix = await AttachByProcessNameAsync(includeExeSuffix: false);
        var withExeSuffix = await AttachByProcessNameAsync(includeExeSuffix: true);

        Assert.Multiple(() =>
        {
            Assert.That(withoutExeSuffix.Pid, Is.GreaterThan(0));
            Assert.That(withExeSuffix.Pid, Is.GreaterThan(0));
            Assert.That(withoutExeSuffix.ProcessName, Is.EqualTo(withExeSuffix.ProcessName));
        });
    }

    private async Task<LaunchAppResponse> LaunchTestAppAsync()
    {
        var exePath = TestAppPaths.FindTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        return await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = workingDirectory,
        });
    }

    private async Task<string> CaptureAttachFailureToCurrentProcessAsync()
    {
        InvalidOperationException? ex = null;
        try
        {
            _ = await _mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["pid"] = Environment.ProcessId
            });
            Assert.Fail("Expected attach_to_app to fail when targeting the test runner process.");
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        return ex!.Message.Split("--- server stderr", StringSplitOptions.None)[0].TrimEnd();
    }

    private async Task CloseAppIfAttachedAsync()
    {
        try
        {
            _ = await _mcp.CallToolAsync<CloseAppResponse>("close_app", new Dictionary<string, object?>
            {
                ["force"] = true,
                ["timeoutMs"] = 2000
            });
        }
        catch
        {
        }
    }

    private async Task<AttachToAppResponse> AttachByProcessNameAsync(bool includeExeSuffix)
    {
        var exePath = TestAppPaths.FindTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;
        var expectedProcessName = Path.GetFileNameWithoutExtension(exePath);

        KillProcessesByName(expectedProcessName);

        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            });

            if (process is null)
            {
                throw new InvalidOperationException("Failed to start test app process.");
            }

            _ = process.WaitForInputIdle(10_000);

            var requestedName = includeExeSuffix
                ? $"{process.ProcessName}.exe"
                : process.ProcessName;

            var attached = await _mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["processName"] = requestedName
            });

            var windows = await _mcp.CallToolAsync<ListWindowsResponse>("list_windows");
            Assert.That(windows.Windows, Is.Not.Empty);

            return attached;
        }
        finally
        {
            await CloseAppIfAttachedAsync();

            if (process is not null)
            {
                KillProcessIfRunning(process.Id);
                try
                {
                    process.Dispose();
                }
                catch
                {
                }
            }

            KillProcessesByName(expectedProcessName);
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void KillProcessIfRunning(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(2000);
        }
        catch
        {
        }
    }

    private static void KillProcessesByName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    _ = process.WaitForExit(2000);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
