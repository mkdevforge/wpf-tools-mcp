using System.Text.Json;
using NUnit.Framework;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class KeyboardInputContractTests
{
    [Test]
    public void Type_text_mode_is_optional_and_round_trips_as_a_string_enum()
    {
        var legacyRequest = new TypeTextRequest(Text: "legacy input");
        var explicitRequest = legacyRequest with { Mode = TextEntryMode.AtSelection };

        var legacyJson = JsonSerializer.Serialize(legacyRequest);
        var explicitJson = JsonSerializer.Serialize(explicitRequest);
        var roundTrip = JsonSerializer.Deserialize<TypeTextRequest>(explicitJson);

        Assert.Multiple(() =>
        {
            Assert.That(legacyRequest.Mode, Is.Null);
            Assert.That(legacyJson, Does.Not.Contain("\"Mode\""));
            Assert.That(explicitJson, Does.Contain("\"Mode\":\"AtSelection\""));
            Assert.That(roundTrip!.Mode, Is.EqualTo(TextEntryMode.AtSelection));
        });
    }

    [Test]
    public void Type_text_response_preserves_the_existing_effects_position()
    {
        var effects = new InteractionEffects(Semantic: true);

        var response = new TypeTextResponse(true, "valuePattern", effects);
        var json = JsonSerializer.Serialize(response);

        Assert.Multiple(() =>
        {
            Assert.That(response.Effects, Is.SameAs(effects));
            Assert.That(response.ModeUsed, Is.EqualTo(TextEntryMode.Replace));
            Assert.That(response.ForegroundFocusRequired, Is.False);
            Assert.That(response.PhysicalInputRequired, Is.False);
            Assert.That(json, Does.Contain("\"ModeUsed\":\"Replace\""));
        });
    }

    [Test]
    public void Keyboard_contract_round_trips_ordered_keys_and_modifier_chords()
    {
        var request = new SendKeysRequest(
        [
            new KeyStroke(KeyboardKey.Enter),
            new KeyStroke(KeyboardKey.A, [KeyboardModifier.Control]),
            new KeyStroke(KeyboardKey.F12, [KeyboardModifier.Shift, KeyboardModifier.Alt])
        ]);

        var json = JsonSerializer.Serialize(request);
        var roundTrip = JsonSerializer.Deserialize<SendKeysRequest>(json);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"Key\":\"Enter\""));
            Assert.That(json, Does.Contain("\"Key\":\"A\",\"Modifiers\":[\"Control\"]"));
            Assert.That(roundTrip!.Sequence.Select(stroke => stroke.Key), Is.EqualTo(
                new[] { KeyboardKey.Enter, KeyboardKey.A, KeyboardKey.F12 }));
            Assert.That(roundTrip.Sequence[0].Modifiers, Is.Null);
            Assert.That(roundTrip.Sequence[2].Modifiers, Is.EqualTo(
                new[] { KeyboardModifier.Shift, KeyboardModifier.Alt }));
        });
    }

    [Test]
    public void Keyboard_stroke_rejects_a_missing_key_during_json_binding()
    {
        var exception = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<KeyStroke>("{}"));

        Assert.That(exception!.Message, Does.Contain("Key"));
    }

    [Test]
    public void Keyboard_key_surface_covers_navigation_text_and_function_keys()
    {
        var keys = Enum.GetValues<KeyboardKey>();

        Assert.Multiple(() =>
        {
            Assert.That(keys, Does.Contain(KeyboardKey.Enter));
            Assert.That(keys, Does.Contain(KeyboardKey.Escape));
            Assert.That(keys, Does.Contain(KeyboardKey.Tab));
            Assert.That(keys, Does.Contain(KeyboardKey.ArrowLeft));
            Assert.That(keys, Does.Contain(KeyboardKey.ArrowUp));
            Assert.That(keys, Does.Contain(KeyboardKey.ArrowRight));
            Assert.That(keys, Does.Contain(KeyboardKey.ArrowDown));
            Assert.That(keys, Does.Contain(KeyboardKey.Digit0));
            Assert.That(keys, Does.Contain(KeyboardKey.Digit9));
            Assert.That(keys, Does.Contain(KeyboardKey.A));
            Assert.That(keys, Does.Contain(KeyboardKey.Z));
            Assert.That(keys, Does.Contain(KeyboardKey.F1));
            Assert.That(keys, Does.Contain(KeyboardKey.F24));
            Assert.That(Enum.GetValues<KeyboardModifier>(), Is.EqualTo(
                new[]
                {
                    KeyboardModifier.Shift,
                    KeyboardModifier.Control,
                    KeyboardModifier.Alt,
                    KeyboardModifier.Windows
                }));
        });
    }

    [Test]
    public void Keyboard_responses_distinguish_requirements_from_observed_effects()
    {
        var response = new SendKeysResponse(
            Sent: true,
            MethodUsed: "keyboard",
            Effects: new InteractionEffects(
                KeyboardInput: true,
                KeyboardFocusChanged: false),
            ForegroundFocusRequired: true,
            PhysicalInputRequired: true);

        var json = JsonSerializer.Serialize(response);
        var roundTrip = JsonSerializer.Deserialize<SendKeysResponse>(json);
        var changedFocusJson = JsonSerializer.Serialize(response with
        {
            Effects = response.Effects! with { KeyboardFocusChanged = true }
        });

        Assert.Multiple(() =>
        {
            Assert.That(roundTrip!.ForegroundFocusRequired, Is.True);
            Assert.That(roundTrip.PhysicalInputRequired, Is.True);
            Assert.That(roundTrip.Effects!.KeyboardInput, Is.True);
            Assert.That(roundTrip.Effects.KeyboardFocusChanged, Is.False);
            Assert.That(json, Does.Not.Contain("\"KeyboardFocusChanged\""));
            Assert.That(changedFocusJson, Does.Contain("\"KeyboardFocusChanged\":true"));
        });
    }
}
