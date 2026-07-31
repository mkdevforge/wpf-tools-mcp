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
