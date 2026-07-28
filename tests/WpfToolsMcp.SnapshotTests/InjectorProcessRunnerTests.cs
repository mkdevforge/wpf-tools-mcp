using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using WpfToolsMcp.Automation;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
public sealed class InjectorProcessRunnerTests
{
    [Test]
    public async Task Isolated_workspace_overrides_hostile_profile_and_suppresses_child_error_dialogs()
    {
        var parentErrorMode = InjectorProcessRunner.GetCurrentErrorMode();
        string workspaceRoot;

        using (var workspace = InjectorLaunchWorkspace.Create())
        {
            workspaceRoot = workspace.RootPath;
            var markerPath = Path.Combine(workspace.RoamingAppDataPath, "Snoop", "fixture.marker");
            var startInfo = CreateFixtureStartInfo("report", "--marker-path", markerPath);
            startInfo.Environment["USERPROFILE"] = @"Z:\hostile-profile";
            startInfo.Environment["APPDATA"] = @"Z:\hostile-profile\roaming";
            startInfo.Environment["LOCALAPPDATA"] = @"Z:\hostile-profile\local";
            startInfo.Environment["TEMP"] = @"Z:\hostile-temp";
            startInfo.Environment["TMP"] = @"Z:\hostile-tmp";
            workspace.ApplyTo(startInfo);

            var result = await InjectorProcessRunner.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            Assert.That(result.ExitCode, Is.Zero, result.Stderr);
            var report = JsonSerializer.Deserialize<FixtureReport>(result.Stdout)
                ?? throw new AssertionException("The fixture did not return a profile report.");
            Assert.Multiple(() =>
            {
                Assert.That(report.UserProfile, Is.EqualTo(workspace.UserProfilePath));
                Assert.That(report.AppData, Is.EqualTo(workspace.RoamingAppDataPath));
                Assert.That(report.LocalAppData, Is.EqualTo(workspace.LocalAppDataPath));
                Assert.That(report.Temp, Is.EqualTo(workspace.TempPath));
                Assert.That(report.Tmp, Is.EqualTo(workspace.TempPath));
                Assert.That(report.UserProfileFolder, Is.Not.Null);
                Assert.That(report.ApplicationDataFolder, Is.Not.Null);
                Assert.That(report.LocalApplicationDataFolder, Is.Not.Null);
                Assert.That(
                    report.ErrorMode & InjectorProcessRunner.SuppressedErrorModeFlags,
                    Is.EqualTo(InjectorProcessRunner.SuppressedErrorModeFlags));
                Assert.That(File.ReadAllText(markerPath), Is.EqualTo("fixture-marker"));
                Assert.That(InjectorProcessRunner.GetCurrentErrorMode(), Is.EqualTo(parentErrorMode));
            });
        }

        Assert.That(Directory.Exists(workspaceRoot), Is.False);
        Assert.That(InjectorProcessRunner.GetCurrentErrorMode(), Is.EqualTo(parentErrorMode));
    }

    [Test]
    public async Task NetFramework_special_folders_used_by_snoop_resolve_inside_workspace()
    {
        using var workspace = InjectorLaunchWorkspace.Create();
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            WorkingDirectory = workspace.RootPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "[pscustomobject]@{" +
            "ApplicationDataFolder=[Environment]::GetFolderPath('ApplicationData');" +
            "LocalApplicationDataFolder=[Environment]::GetFolderPath('LocalApplicationData')" +
            "} | ConvertTo-Json -Compress");
        workspace.ApplyTo(startInfo);

        var result = await InjectorProcessRunner.RunAsync(
            startInfo,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.That(result.ExitCode, Is.Zero, result.Stderr);
        var report = JsonSerializer.Deserialize<NetFrameworkSpecialFolderReport>(result.Stdout)
            ?? throw new AssertionException("The .NET Framework probe did not return a special-folder report.");
        Assert.Multiple(() =>
        {
            Assert.That(report.ApplicationDataFolder, Is.EqualTo(workspace.RoamingAppDataPath));
            Assert.That(report.LocalApplicationDataFolder, Is.EqualTo(workspace.LocalAppDataPath));
        });
    }

    [Test]
    public async Task Nonzero_exit_drains_both_streams_concurrently_and_bounds_capture()
    {
        using var workspace = InjectorLaunchWorkspace.Create();
        var startInfo = CreateFixtureStartInfo(
            "emit",
            "--exit-code", "23",
            "--blocks", "256");
        workspace.ApplyTo(startInfo);

        var result = await InjectorProcessRunner.RunAsync(
            startInfo,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(23));
            Assert.That(result.Stdout, Does.StartWith("stdout-0000-"));
            Assert.That(result.Stderr, Does.StartWith("stderr-0000-"));
            Assert.That(result.Stdout, Does.Contain("output truncated; observed"));
            Assert.That(result.Stderr, Does.Contain("output truncated; observed"));
            Assert.That(result.Stdout, Does.EndWith("stdout-final-diagnostic-marker" + Environment.NewLine));
            Assert.That(result.Stderr, Does.EndWith("stderr-final-diagnostic-marker" + Environment.NewLine));
            Assert.That(result.Stdout.Length, Is.LessThan(17_000));
            Assert.That(result.Stderr.Length, Is.LessThan(17_000));
        });
    }

    [Test]
    public void Start_failure_identifies_the_launcher_and_working_directory()
    {
        using var workspace = InjectorLaunchWorkspace.Create();
        var missingExecutable = Path.Combine(workspace.RootPath, "missing-launcher.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = missingExecutable,
            WorkingDirectory = workspace.RootPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        workspace.ApplyTo(startInfo);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            _ = await InjectorProcessRunner.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(5),
                CancellationToken.None));

        Assert.That(exception, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain(missingExecutable));
            Assert.That(exception.Message, Does.Contain(workspace.RootPath));
            Assert.That(exception.InnerException, Is.Not.Null);
        });
    }

    [Test]
    public async Task Timeout_kills_root_and_child_and_reports_launcher_context()
    {
        using var workspace = InjectorLaunchWorkspace.Create();
        var rootPidPath = Path.Combine(workspace.RootPath, "root.pid");
        var childPidPath = Path.Combine(workspace.RootPath, "child.pid");
        var startInfo = CreateFixtureStartInfo(
            "spawn-tree",
            "--root-pid-path", rootPidPath,
            "--child-pid-path", childPidPath);
        workspace.ApplyTo(startInfo);
        var runnerTask = InjectorProcessRunner.RunAsync(
            startInfo,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        Process? rootProcess = null;
        Process? childProcess = null;

        try
        {
            var rootPid = await ReadPidAsync(rootPidPath);
            var childPid = await ReadPidAsync(childPidPath);
            rootProcess = OpenFixtureProcess(rootPid);
            childProcess = OpenFixtureProcess(childPid);
            var exception = Assert.ThrowsAsync<TimeoutException>(async () =>
                _ = await runnerTask);

            Assert.That(exception, Is.Not.Null);
            await AssertProcessExitedAsync(rootProcess);
            await AssertProcessExitedAsync(childProcess);
            Assert.Multiple(() =>
            {
                Assert.That(exception!.Message, Does.Contain(startInfo.FileName));
                Assert.That(exception.Message, Does.Contain($"PID {rootPid.ToString(CultureInfo.InvariantCulture)}"));
                Assert.That(exception.Message, Does.Contain("timed out after 5000 ms"));
                Assert.That(exception.Message, Does.Contain("tree kill requested=true"));
                Assert.That(exception.Message, Does.Contain("spawned root="));
                Assert.That(exception.Message, Does.Contain("--- stdout ---"));
                Assert.That(exception.Message, Does.Contain("--- stderr ---"));
            });
        }
        finally
        {
            await BestEffortKillFixtureProcessesAsync(rootProcess, childProcess);
            await ObserveRunnerCompletionAsync(runnerTask);
            rootProcess?.Dispose();
            childProcess?.Dispose();
        }
    }

    [Test]
    public async Task Caller_cancellation_kills_root_and_child_and_remains_cancellation()
    {
        using var workspace = InjectorLaunchWorkspace.Create();
        var rootPidPath = Path.Combine(workspace.RootPath, "root.pid");
        var childPidPath = Path.Combine(workspace.RootPath, "child.pid");
        var startInfo = CreateFixtureStartInfo(
            "spawn-tree",
            "--root-pid-path", rootPidPath,
            "--child-pid-path", childPidPath);
        workspace.ApplyTo(startInfo);
        using var cancellation = new CancellationTokenSource();
        var runnerTask = InjectorProcessRunner.RunAsync(
            startInfo,
            TimeSpan.FromSeconds(10),
            cancellation.Token);
        Process? rootProcess = null;
        Process? childProcess = null;

        try
        {
            var rootPid = await ReadPidAsync(rootPidPath);
            var childPid = await ReadPidAsync(childPidPath);
            rootProcess = OpenFixtureProcess(rootPid);
            childProcess = OpenFixtureProcess(childPid);
            cancellation.Cancel();
            var exception = Assert.ThrowsAsync<OperationCanceledException>(async () =>
                _ = await runnerTask);

            Assert.That(exception, Is.Not.Null);
            await AssertProcessExitedAsync(rootProcess);
            await AssertProcessExitedAsync(childProcess);
            Assert.Multiple(() =>
            {
                Assert.That(exception!.CancellationToken, Is.EqualTo(cancellation.Token));
                Assert.That(exception.Message, Does.Contain(startInfo.FileName));
                Assert.That(exception.Message, Does.Contain($"PID {rootPid.ToString(CultureInfo.InvariantCulture)}"));
                Assert.That(exception.Message, Does.Contain("was canceled"));
                Assert.That(exception.Message, Does.Contain("tree kill requested=true"));
                Assert.That(exception.Message, Does.Contain("spawned root="));
                Assert.That(exception.Message, Does.Contain("--- stdout ---"));
                Assert.That(exception.Message, Does.Contain("--- stderr ---"));
            });
        }
        finally
        {
            cancellation.Cancel();
            await BestEffortKillFixtureProcessesAsync(rootProcess, childProcess);
            await ObserveRunnerCompletionAsync(runnerTask);
            rootProcess?.Dispose();
            childProcess?.Dispose();
        }
    }

    [TestCase(null, InjectorProcessRunner.DefaultTimeoutMs)]
    [TestCase("", InjectorProcessRunner.DefaultTimeoutMs)]
    [TestCase("not-a-number", InjectorProcessRunner.DefaultTimeoutMs)]
    [TestCase("0", InjectorProcessRunner.DefaultTimeoutMs)]
    [TestCase("-1", InjectorProcessRunner.DefaultTimeoutMs)]
    [TestCase("1", InjectorProcessRunner.MinimumTimeoutMs)]
    [TestCase("25000", 25000)]
    [TestCase("999999", InjectorProcessRunner.MaximumTimeoutMs)]
    public void Timeout_configuration_is_validated_and_clamped(string? rawValue, int expected) =>
        Assert.That(InjectorProcessRunner.ParseTimeoutMilliseconds(rawValue), Is.EqualTo(expected));

    private static ProcessStartInfo CreateFixtureStartInfo(params string[] arguments)
    {
        var executablePath = FindFixtureExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string FindFixtureExecutable()
    {
        var binRoot = Path.Combine(
            RepoRoot.Find(),
            "tests",
            "WpfToolsMcp.ProcessFixture",
            "bin");
        if (!Directory.Exists(binRoot))
        {
            throw new DirectoryNotFoundException($"Could not find process fixture output under '{binRoot}'.");
        }

        return Directory.EnumerateFiles(
                binRoot,
                "WpfToolsMcp.ProcessFixture.exe",
                SearchOption.AllDirectories)
            .Where(path => path.Contains("net8.0", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new FileNotFoundException($"Could not find the process fixture executable under '{binRoot}'.");
    }

    private static async Task<int> ReadPidAsync(string path)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(3))
        {
            try
            {
                if (File.Exists(path) &&
                    int.TryParse(
                        await File.ReadAllTextAsync(path),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var pid))
                {
                    return pid;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(25);
        }

        throw new AssertionException($"Fixture did not write PID file '{path}' within the bounded wait.");
    }

    private static Process OpenFixtureProcess(int pid)
    {
        var process = Process.GetProcessById(pid);
        try
        {
            if (!IsProcessFixture(process))
            {
                throw new AssertionException(
                    $"Recorded PID {pid.ToString(CultureInfo.InvariantCulture)} is not the process fixture.");
            }

            _ = process.StartTime;
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static async Task AssertProcessExitedAsync(Process process)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(3))
        {
            try
            {
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail(
            $"Process {process.Id.ToString(CultureInfo.InvariantCulture)} remained alive after tree termination.");
    }

    private static async Task BestEffortKillFixtureProcessesAsync(params Process?[] processes)
    {
        foreach (var process in processes.Where(process => process is not null).Cast<Process>())
        {
            try
            {
                if (!process.HasExited && IsProcessFixture(process))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            catch
            {
                // Test teardown must remain best effort even when the process already exited.
            }
        }
    }

    private static async Task ObserveRunnerCompletionAsync(Task runnerTask)
    {
        try
        {
            await runnerTask.WaitAsync(TimeSpan.FromSeconds(12));
        }
        catch
        {
            // The test body owns the result; teardown only observes completion.
        }
    }

    private static bool IsProcessFixture(Process process)
    {
        try
        {
            return string.Equals(
                Path.GetFileName(process.MainModule?.FileName),
                "WpfToolsMcp.ProcessFixture.exe",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private sealed record FixtureReport(
        int ProcessId,
        string? UserProfile,
        string? AppData,
        string? LocalAppData,
        string? Temp,
        string? Tmp,
        string? UserProfileFolder,
        string? ApplicationDataFolder,
        string? LocalApplicationDataFolder,
        uint ErrorMode);

    private sealed record NetFrameworkSpecialFolderReport(
        string? ApplicationDataFolder,
        string? LocalApplicationDataFolder);
}
