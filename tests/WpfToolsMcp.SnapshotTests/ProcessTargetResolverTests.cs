using System.ComponentModel;
using System.Diagnostics;
using NUnit.Framework;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class ProcessTargetResolverTests
{
    [TestCase("Sample.App", "Sample.App")]
    [TestCase("Sample.App.exe", "Sample.App")]
    [TestCase(@"C:\apps\Sample.App.exe", "Sample.App")]
    public void NormalizeProcessName_only_strips_a_terminal_exe_suffix(string input, string expected)
    {
        Assert.That(ProcessTargetResolver.NormalizeProcessName(input), Is.EqualTo(expected));
    }

    [Test]
    public void ProcessInstanceId_round_trips_to_the_same_live_process()
    {
        var byPid = ProcessTargetResolver.ResolveByPid(Environment.ProcessId);
        var byInstance = ProcessTargetResolver.Resolve(
            new AttachToAppRequest(ProcessInstanceId: byPid.Identity.Value));
        using var currentProcess = Process.GetCurrentProcess();
        var expectedExecutablePath = currentProcess.MainModule?.FileName;

        Assert.Multiple(() =>
        {
            Assert.That(byInstance.Identity, Is.EqualTo(byPid.Identity));
            Assert.That(byInstance.ProcessName, Is.EqualTo(currentProcess.ProcessName));
            Assert.That(byInstance.StartTimeUtc, Is.EqualTo(byPid.StartTimeUtc));
            Assert.That(byPid.ExecutablePath, Is.EqualTo(expectedExecutablePath).IgnoreCase);
            Assert.That(byInstance.ExecutablePath, Is.EqualTo(byPid.ExecutablePath).IgnoreCase);
            Assert.That(byPid.ExecutablePathUnavailableReason, Is.Null);
            Assert.That(byInstance.ExecutablePathUnavailableReason, Is.Null);
        });
    }

    [Test]
    public void Executable_path_is_best_effort_when_the_public_process_api_is_unavailable()
    {
        using var unassociatedProcess = new Process();
        var observation = ProcessTargetResolver.GetExecutablePathBestEffort(unassociatedProcess);

        Assert.Multiple(() =>
        {
            Assert.That(observation.ExecutablePath, Is.Null);
            Assert.That(observation.UnavailableReason, Does.StartWith("mainModuleReadFailed:"));
            Assert.That(observation.UnavailableReason, Does.Contain(nameof(InvalidOperationException)));
            Assert.That(observation.UnavailableReason, Has.Length.LessThanOrEqualTo(256));
        });
    }

    [Test]
    public void Oversized_executable_paths_are_omitted_instead_of_returned_as_invalid_prefixes()
    {
        var path = @"C:\" + new string('x', ProcessTargetResolver.MaximumExecutablePathLength + 10);

        var observation = ProcessTargetResolver.CreateExecutablePathObservation(path);

        Assert.Multiple(() =>
        {
            Assert.That(observation.ExecutablePath, Is.Null);
            Assert.That(observation.UnavailableReason, Does.StartWith("mainModuleFileNameOmitted:"));
            Assert.That(observation.UnavailableReason, Does.Contain($"actualLength={path.Length}"));
        });
    }

    [Test]
    public void Executable_path_failures_keep_bounded_windows_diagnostics()
    {
        var exception = new Win32Exception(5, "Access to the process module was denied.");

        var reason = ProcessTargetResolver.FormatExecutablePathFailure(exception);

        Assert.Multiple(() =>
        {
            Assert.That(reason, Does.StartWith("mainModuleReadFailed:System.ComponentModel.Win32Exception;"));
            Assert.That(reason, Does.Contain("hresult=0x"));
            Assert.That(reason, Does.Contain("nativeError=5"));
            Assert.That(reason, Does.Contain("Access to the process module was denied."));
            Assert.That(reason, Has.Length.LessThanOrEqualTo(256));
        });
    }

    [Test]
    public void Diagnostic_text_bounds_do_not_split_utf16_surrogate_pairs()
    {
        var value = new string('x', 255) + "\U0001F680" + "tail";

        var bounded = ProcessTargetResolver.Bound(value, 256);

        Assert.Multiple(() =>
        {
            Assert.That(bounded, Has.Length.EqualTo(255));
            Assert.That(bounded, Does.EndWith("x"));
            Assert.That(bounded.Any(char.IsSurrogate), Is.False);
        });
    }

    [Test]
    public void StaleProcessInstanceId_never_falls_back_to_the_current_pid_owner()
    {
        var current = ProcessTargetResolver.ResolveByPid(Environment.ProcessId);
        var staleId = new ProcessInstanceIdentity(
            current.Identity.Pid,
            current.Identity.StartTimeFileTimeUtc - 1).Value;

        var exception = Assert.Catch<InvalidOperationException>(() =>
            ProcessTargetResolver.Resolve(new AttachToAppRequest(ProcessInstanceId: staleId)));

        Assert.That(exception!.Message, Does.StartWith("stale_process_candidate:"));
    }
}
