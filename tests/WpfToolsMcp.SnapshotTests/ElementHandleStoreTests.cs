using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class ElementHandleStoreTests
{
    [Test]
    public void Shared_agent_handle_is_released_only_after_its_last_public_handle()
    {
        var store = new AutomationController.ElementHandleStore(capacity: 10);
        var first = RegisterWpf(store, "agent_shared", "/Window/Button[1]");
        var second = RegisterWpf(store, "agent_shared", "/Window/Button[1]");

        Assert.That(second, Is.Not.EqualTo(first));

        var firstRelease = store.Release(first);
        Assert.Multiple(() =>
        {
            Assert.That(firstRelease.Released, Is.True);
            Assert.That(firstRelease.WpfAgentElementIdToRelease, Is.Null);
            Assert.That(store.TryGet(second, out _), Is.True);
        });

        var secondRelease = store.Release(second);
        Assert.Multiple(() =>
        {
            Assert.That(secondRelease.Released, Is.True);
            Assert.That(secondRelease.WpfAgentElementIdToRelease, Is.EqualTo("agent_shared"));
        });
    }

    [Test]
    public void Eviction_removes_only_the_evicted_public_reference()
    {
        var store = new AutomationController.ElementHandleStore(capacity: 2);
        var evicted = RegisterWpf(store, "agent_shared", "/Window/Button[1]");
        var retained = RegisterWpf(store, "agent_shared", "/Window/Button[1]");

        _ = RegisterWpf(store, "agent_other", "/Window/Button[2]");

        Assert.Multiple(() =>
        {
            Assert.That(store.TryGet(evicted, out _), Is.False);
            Assert.That(store.TryGet(retained, out _), Is.True);
        });

        var release = store.Release(retained);
        Assert.That(release.WpfAgentElementIdToRelease, Is.EqualTo("agent_shared"));
    }

    [Test]
    public void Updating_a_recovered_handle_moves_its_agent_reference()
    {
        var store = new AutomationController.ElementHandleStore(capacity: 10);
        var recovered = RegisterWpf(store, "agent_old", "/Window/Button[1]");
        var oldAlias = RegisterWpf(store, "agent_old", "/Window/Button[1]");

        var updated = store.TryUpdateWpfResolution(
            recovered,
            new ElementRef(
                Type: "Button",
                AutomationId: "PrimaryButton",
                Name: "Primary",
                XPath: "/Window/Button[1]",
                ElementIdWpf: "agent_new"));

        var oldRelease = store.Release(oldAlias);
        var recoveredRelease = store.Release(recovered);

        Assert.Multiple(() =>
        {
            Assert.That(updated, Is.True);
            Assert.That(oldRelease.WpfAgentElementIdToRelease, Is.EqualTo("agent_old"));
            Assert.That(recoveredRelease.WpfAgentElementIdToRelease, Is.EqualTo("agent_new"));
        });
    }

    private static string RegisterWpf(
        AutomationController.ElementHandleStore store,
        string agentElementId,
        string xpath)
    {
        return store.RegisterWpf(
            windowHandle: 42,
            xpath,
            agentElementId,
            type: "Button",
            automationId: "PrimaryButton",
            name: "Primary",
            className: "System.Windows.Controls.Button");
    }
}
