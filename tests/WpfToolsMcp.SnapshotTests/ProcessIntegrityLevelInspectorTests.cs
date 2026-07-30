using WpfToolsMcp.Automation;

namespace WpfToolsMcp.SnapshotTests;

public sealed class ProcessIntegrityLevelInspectorTests
{
    [TestCase(0x2000, 0x2000, (int)ProcessIntegrityLevelComparison.Same)]
    [TestCase(0x3000, 0x2000, (int)ProcessIntegrityLevelComparison.CurrentHigher)]
    [TestCase(0x2000, 0x3000, (int)ProcessIntegrityLevelComparison.TargetHigher)]
    [TestCase(0x2100, 0x3000, (int)ProcessIntegrityLevelComparison.TargetHigher)]
    public void Integrity_rid_comparison_is_numeric_and_deterministic(
        int currentRid,
        int targetRid,
        int expected)
    {
        Assert.That(
            (int)ProcessIntegrityLevelInspector.CompareIntegrityRids(currentRid, targetRid),
            Is.EqualTo(expected));
    }

    [Test]
    public void Integrity_rid_comparison_rejects_unmeasured_values()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ProcessIntegrityLevelInspector.CompareIntegrityRids(-1, 0x2000));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ProcessIntegrityLevelInspector.CompareIntegrityRids(0x2000, -1));
        });
    }
}
