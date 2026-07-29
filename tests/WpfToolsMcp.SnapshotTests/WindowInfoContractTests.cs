using System.Text.Json;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class WindowInfoContractTests
{
    [Test]
    public void Native_window_context_is_additive_and_round_trips()
    {
        var legacy = new WindowInfo(
            Title: "Main",
            Handle: 101,
            Bounds: new Rect(1, 2, 300, 200),
            IsVisible: true,
            IsEnabled: true);
        var nativeDialog = legacy with
        {
            Title = "Open fixture",
            Handle = 202,
            OwnerHandle = 101,
            IsModal = true,
            FrameworkId = "Win32"
        };

        var legacyJson = JsonSerializer.Serialize(legacy);
        var nativeJson = JsonSerializer.Serialize(nativeDialog);
        var roundTrip = JsonSerializer.Deserialize<WindowInfo>(nativeJson);

        Assert.Multiple(() =>
        {
            Assert.That(legacyJson, Does.Not.Contain("OwnerHandle"));
            Assert.That(legacyJson, Does.Not.Contain("IsModal"));
            Assert.That(legacyJson, Does.Not.Contain("FrameworkId"));
            Assert.That(nativeJson, Does.Contain("\"OwnerHandle\":101"));
            Assert.That(nativeJson, Does.Contain("\"IsModal\":true"));
            Assert.That(nativeJson, Does.Contain("\"FrameworkId\":\"Win32\""));
            Assert.That(roundTrip, Is.EqualTo(nativeDialog));
        });
    }

    [Test]
    public void Existing_positional_constructor_shape_is_preserved()
    {
        var constructor = typeof(WindowInfo).GetConstructors().Single(candidate => candidate.IsPublic);

        Assert.That(
            constructor.GetParameters().Select(parameter => parameter.Name),
            Is.EqualTo(new[] { "Title", "Handle", "Bounds", "IsVisible", "IsEnabled" }));
    }
}
