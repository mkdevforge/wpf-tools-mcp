using WpfToolsMcp.Automation;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class WaitWindowIdentityTests
{
    [Test]
    public void Same_identity_is_preserved()
    {
        var identity = CreateIdentity();

        Assert.That(
            AutomationController.SameWaitWindowIdentity(identity, identity),
            Is.True);
    }

    [TestCase(101u, "#32770", 42L)]
    [TestCase(100u, "ReplacementClass", 42L)]
    [TestCase(100u, "#32770", 43L)]
    public void Changed_native_identity_marks_the_handle_as_replaced(
        uint threadId,
        string className,
        long? ownerHandle)
    {
        var expected = CreateIdentity();
        var actual = new AutomationController.WaitWindowIdentity(
            expected.Handle,
            threadId,
            className,
            ownerHandle);

        Assert.That(
            AutomationController.SameWaitWindowIdentity(expected, actual),
            Is.False);
    }

    [Test]
    public void Unavailable_class_metadata_does_not_claim_replacement()
    {
        var expected = CreateIdentity();
        var actual = expected with { ClassName = string.Empty };

        Assert.That(
            AutomationController.SameWaitWindowIdentity(expected, actual),
            Is.True);
    }

    private static AutomationController.WaitWindowIdentity CreateIdentity() =>
        new(
            Handle: 1234,
            ThreadId: 100,
            ClassName: "#32770",
            OwnerHandle: 42);
}
