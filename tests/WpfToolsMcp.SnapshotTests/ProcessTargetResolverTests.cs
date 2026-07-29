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

        Assert.Multiple(() =>
        {
            Assert.That(byInstance.Identity, Is.EqualTo(byPid.Identity));
            Assert.That(byInstance.ProcessName, Is.EqualTo(Process.GetCurrentProcess().ProcessName));
            Assert.That(byInstance.StartTimeUtc, Is.EqualTo(byPid.StartTimeUtc));
        });
    }

    [Test]
    public void StaleProcessInstanceId_never_falls_back_to_the_current_pid_owner()
    {
        var current = ProcessTargetResolver.ResolveByPid(Environment.ProcessId);
        var staleId = new ProcessInstanceIdentity(
            current.Identity.Pid,
            current.Identity.StartTimeFileTimeUtc - 1).Value;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProcessTargetResolver.Resolve(new AttachToAppRequest(ProcessInstanceId: staleId)));

        Assert.That(exception!.Message, Does.StartWith("stale_process_candidate:"));
    }
}
