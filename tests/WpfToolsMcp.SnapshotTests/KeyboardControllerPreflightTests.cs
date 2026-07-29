using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class KeyboardControllerPreflightTests
{
    [Test]
    public void Send_keys_blocks_physical_input_before_requiring_an_attachment()
    {
        using var controller = new AutomationController();
        var request = new SendKeysRequest(
            Sequence: [new KeyStroke(KeyboardKey.Enter)],
            InteractionPolicy: new InteractionPolicy(AllowPhysicalInput: false));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.SendKeysAsync(request));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("interaction_policy_blocked"));
            Assert.That(exception.Message, Does.Contain("allowPhysicalInput=false"));
            Assert.That(exception.Message, Does.Not.Contain("not_attached").IgnoreCase);
        });
    }

    [Test]
    public void Send_keys_validates_the_complete_sequence_before_policy_or_attachment()
    {
        using var controller = new AutomationController();
        var invalidRequests = new[]
        {
            new SendKeysRequest(
                Sequence: [],
                InteractionPolicy: new InteractionPolicy(AllowPhysicalInput: false)),
            new SendKeysRequest(
                Sequence: [new KeyStroke((KeyboardKey)int.MaxValue)],
                InteractionPolicy: new InteractionPolicy(AllowPhysicalInput: false))
        };

        var exceptions = invalidRequests
            .Select(request => Assert.CatchAsync<ArgumentException>(() =>
                controller.SendKeysAsync(request)))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(exceptions[0]!.Message, Does.Contain("sequence must contain between 1 and 100 steps"));
            Assert.That(exceptions[1]!.Message, Does.Contain("Unknown keyboard key"));
            Assert.That(
                exceptions.All(ex => !ex!.Message.Contains("interaction_policy_blocked", StringComparison.OrdinalIgnoreCase)),
                Is.True);
            Assert.That(
                exceptions.All(ex => !ex!.Message.Contains("not_attached", StringComparison.OrdinalIgnoreCase)),
                Is.True);
        });
    }
}
