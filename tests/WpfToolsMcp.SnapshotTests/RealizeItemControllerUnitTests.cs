using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class RealizeItemControllerUnitTests
{
    private static readonly int[] RuntimeId = [42, 7, 11];

    [Test]
    public void VirtualizedItem_pattern_is_authoritative_over_tree_ancestry()
    {
        var placeholderExposedInTree = AutomationController.ClassifyRealizeItemTargetState(
            isWithinWindow: true,
            supportsVirtualizedItemPattern: true);
        var realized = AutomationController.ClassifyRealizeItemTargetState(
            isWithinWindow: true,
            supportsVirtualizedItemPattern: false);
        var unsupportedPlaceholder = AutomationController.ClassifyRealizeItemTargetState(
            isWithinWindow: false,
            supportsVirtualizedItemPattern: false);

        Assert.Multiple(() =>
        {
            Assert.That(placeholderExposedInTree, Is.EqualTo(RealizeItemTargetState.Virtualized));
            Assert.That(realized, Is.EqualTo(RealizeItemTargetState.AlreadyRealized));
            Assert.That(unsupportedPlaceholder, Is.EqualTo(RealizeItemTargetState.Unsupported));
        });
    }

    [Test]
    public void Realized_identity_requires_matching_window_process_and_runtime_identity()
    {
        var status = AutomationController.ClassifyRealizedItemIdentity(
            expectedWindowHandle: 123,
            currentWindowHandle: 123,
            expectedProcessId: 456,
            targetProcessIdBeforePath: 456,
            targetProcessIdAfterPath: 456,
            resolvedProcessId: 456,
            targetRuntimeIdBeforePath: RuntimeId,
            targetRuntimeIdAfterPath: [.. RuntimeId],
            resolvedRuntimeId: [.. RuntimeId],
            targetWithinWindow: true,
            resolvedWithinWindow: true);

        Assert.That(status, Is.EqualTo(AutomationController.RealizedItemIdentityStatus.Verified));
    }

    [TestCase(false, true, 123)]
    [TestCase(true, false, 123)]
    [TestCase(true, true, 999)]
    public void Realized_identity_rejects_a_different_window(
        bool targetWithinWindow,
        bool resolvedWithinWindow,
        long currentWindowHandle)
    {
        var status = AutomationController.ClassifyRealizedItemIdentity(
            expectedWindowHandle: 123,
            currentWindowHandle,
            expectedProcessId: 456,
            targetProcessIdBeforePath: 456,
            targetProcessIdAfterPath: 456,
            resolvedProcessId: 456,
            targetRuntimeIdBeforePath: RuntimeId,
            targetRuntimeIdAfterPath: RuntimeId,
            resolvedRuntimeId: RuntimeId,
            targetWithinWindow,
            resolvedWithinWindow);

        Assert.That(status, Is.EqualTo(AutomationController.RealizedItemIdentityStatus.WindowChanged));
    }

    [Test]
    public void Realized_identity_rejects_a_different_process()
    {
        var status = AutomationController.ClassifyRealizedItemIdentity(
            expectedWindowHandle: 123,
            currentWindowHandle: 123,
            expectedProcessId: 456,
            targetProcessIdBeforePath: 456,
            targetProcessIdAfterPath: 456,
            resolvedProcessId: 789,
            targetRuntimeIdBeforePath: RuntimeId,
            targetRuntimeIdAfterPath: RuntimeId,
            resolvedRuntimeId: RuntimeId,
            targetWithinWindow: true,
            resolvedWithinWindow: true);

        Assert.That(status, Is.EqualTo(AutomationController.RealizedItemIdentityStatus.ProcessChanged));
    }

    [Test]
    public void Realized_identity_distinguishes_changed_target_from_recycled_path()
    {
        var changed = AutomationController.ClassifyRealizedItemIdentity(
            expectedWindowHandle: 123,
            currentWindowHandle: 123,
            expectedProcessId: 456,
            targetProcessIdBeforePath: 456,
            targetProcessIdAfterPath: 456,
            resolvedProcessId: 456,
            targetRuntimeIdBeforePath: RuntimeId,
            targetRuntimeIdAfterPath: [42, 7, 12],
            resolvedRuntimeId: [42, 7, 12],
            targetWithinWindow: true,
            resolvedWithinWindow: true);
        var recycled = AutomationController.ClassifyRealizedItemIdentity(
            expectedWindowHandle: 123,
            currentWindowHandle: 123,
            expectedProcessId: 456,
            targetProcessIdBeforePath: 456,
            targetProcessIdAfterPath: 456,
            resolvedProcessId: 456,
            targetRuntimeIdBeforePath: RuntimeId,
            targetRuntimeIdAfterPath: RuntimeId,
            resolvedRuntimeId: [42, 7, 99],
            targetWithinWindow: true,
            resolvedWithinWindow: true);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(AutomationController.RealizedItemIdentityStatus.IdentityChanged));
            Assert.That(recycled, Is.EqualTo(AutomationController.RealizedItemIdentityStatus.IdentityRecycled));
        });
    }

    [Test]
    public void Realized_identity_without_runtime_identity_is_not_reusable()
    {
        var status = AutomationController.ClassifyRealizedItemIdentity(
            expectedWindowHandle: 123,
            currentWindowHandle: 123,
            expectedProcessId: 456,
            targetProcessIdBeforePath: 456,
            targetProcessIdAfterPath: 456,
            resolvedProcessId: 456,
            targetRuntimeIdBeforePath: RuntimeId,
            targetRuntimeIdAfterPath: RuntimeId,
            resolvedRuntimeId: null,
            targetWithinWindow: true,
            resolvedWithinWindow: true);

        Assert.That(status, Is.EqualTo(AutomationController.RealizedItemIdentityStatus.IdentityUnavailable));
    }

    [Test]
    public void Controller_rejects_multiple_container_sources_before_attachment()
    {
        using var controller = new AutomationController();

        var exception = Assert.ThrowsAsync<ArgumentException>(
            async () => await controller.RealizeItemAsync(
                containerLocator: new ElementLocator(AutomationId: "Items"),
                containerElementId: "uia_existing",
                index: 0,
                name: null));

        Assert.That(exception!.Message, Does.Contain("exactly one"));
    }

    [Test]
    public void Controller_rejects_provider_bounds_before_attachment()
    {
        using var controller = new AutomationController();

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await controller.RealizeItemAsync(
                containerLocator: new ElementLocator(AutomationId: "Items"),
                containerElementId: null,
                index: 0,
                name: null,
                maxProviderCalls: RealizeItemLimits.MaximumProviderCalls + 1));
    }
}
