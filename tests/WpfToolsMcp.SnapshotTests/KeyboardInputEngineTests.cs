using FlaUI.Core.WindowsAPI;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class KeyboardInputEngineTests
{
    [TestCase(null, false, TextEntryMode.AtSelection)]
    [TestCase(null, true, TextEntryMode.Replace)]
    [TestCase(TextEntryMode.Replace, false, TextEntryMode.Replace)]
    [TestCase(TextEntryMode.Append, true, TextEntryMode.Append)]
    [TestCase(TextEntryMode.AtSelection, true, TextEntryMode.AtSelection)]
    public void Text_entry_mode_preserves_legacy_defaults(
        TextEntryMode? requestedMode,
        bool hasTarget,
        TextEntryMode expected) =>
        Assert.That(
            KeyboardInputEngine.ResolveTextEntryMode(requestedMode, hasTarget),
            Is.EqualTo(expected));

    [TestCase(TextEntryMode.Replace, false, true)]
    [TestCase(TextEntryMode.Replace, true, true)]
    [TestCase(TextEntryMode.Append, false, true)]
    [TestCase(TextEntryMode.Append, true, false)]
    [TestCase(TextEntryMode.AtSelection, false, false)]
    [TestCase(TextEntryMode.AtSelection, true, false)]
    public void Semantic_value_pattern_selection_avoids_reading_password_values(
        TextEntryMode mode,
        bool isPassword,
        bool expected) =>
        Assert.That(
            KeyboardInputEngine.CanUseSemanticValuePattern(mode, isPassword),
            Is.EqualTo(expected));

    [TestCase(false, true)]
    [TestCase(true, false)]
    public void Value_pattern_verification_never_reads_password_text(
        bool isPassword,
        bool expected) =>
        Assert.That(
            KeyboardInputEngine.CanReadValuePatternText(isPassword),
            Is.EqualTo(expected));

    [Test]
    public void Every_public_keyboard_key_maps_to_a_virtual_key()
    {
        foreach (var key in Enum.GetValues<KeyboardKey>())
        {
            Assert.That(
                KeyboardInputEngine.ToVirtualKey(key),
                Is.Not.EqualTo((VirtualKeyShort)0),
                $"{key} must have a non-zero virtual-key mapping.");
        }

        Assert.Multiple(() =>
        {
            Assert.That(KeyboardInputEngine.ToVirtualKey(KeyboardKey.Enter), Is.EqualTo(VirtualKeyShort.RETURN));
            Assert.That(KeyboardInputEngine.ToVirtualKey(KeyboardKey.ArrowLeft), Is.EqualTo(VirtualKeyShort.LEFT));
            Assert.That(KeyboardInputEngine.ToVirtualKey(KeyboardKey.Digit9), Is.EqualTo(VirtualKeyShort.KEY_9));
            Assert.That(KeyboardInputEngine.ToVirtualKey(KeyboardKey.Z), Is.EqualTo(VirtualKeyShort.KEY_Z));
            Assert.That(KeyboardInputEngine.ToVirtualKey(KeyboardKey.F24), Is.EqualTo(VirtualKeyShort.F24));
            Assert.That(
                KeyboardInputEngine.ToVirtualKey(KeyboardModifier.Windows),
                Is.EqualTo(VirtualKeyShort.LWIN));
        });
    }

    [Test]
    public void Sequence_sends_chords_in_order_and_releases_each_chord_in_reverse()
    {
        var sink = new RecordingKeyboardInputSink();

        KeyboardInputEngine.SendSequence(
            [
                new KeyStroke(KeyboardKey.A, [KeyboardModifier.Control, KeyboardModifier.Shift]),
                new KeyStroke(KeyboardKey.Enter),
                new KeyStroke(KeyboardKey.F12, [KeyboardModifier.Alt]),
                new KeyStroke(KeyboardKey.D, [KeyboardModifier.Windows])
            ],
            sink);

        Assert.That(sink.Events, Is.EqualTo(new[]
        {
            "press:CONTROL",
            "press:SHIFT",
            "press:KEY_A",
            "release:KEY_A",
            "release:SHIFT",
            "release:CONTROL",
            "press:ENTER",
            "release:ENTER",
            "press:ALT",
            "press:F12",
            "release:F12",
            "release:ALT",
            "press:LWIN",
            "press:KEY_D",
            "release:KEY_D",
            "release:LWIN"
        }));
    }

    [Test]
    public void Sequence_is_fully_validated_before_any_key_is_pressed()
    {
        var sink = new RecordingKeyboardInputSink();
        var sequence = new[]
        {
            new KeyStroke(KeyboardKey.A),
            new KeyStroke((KeyboardKey)int.MaxValue)
        };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            KeyboardInputEngine.SendSequence(sequence, sink));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("Unknown keyboard key"));
            Assert.That(sink.Events, Is.Empty);
        });
    }

    [Test]
    public void Prepared_sequence_is_detached_from_mutable_request_collections()
    {
        var modifiers = new List<KeyboardModifier> { KeyboardModifier.Control };
        var requestSequence = new List<KeyStroke> { new(KeyboardKey.A, modifiers) };
        var prepared = KeyboardInputEngine.BuildSequence(requestSequence);
        modifiers[0] = KeyboardModifier.Alt;
        requestSequence.Clear();
        var sink = new RecordingKeyboardInputSink();

        KeyboardInputEngine.SendPreparedSequence(prepared, sink);

        Assert.That(sink.Events, Is.EqualTo(new[]
        {
            "press:CONTROL",
            "press:KEY_A",
            "release:KEY_A",
            "release:CONTROL"
        }));
    }

    [Test]
    public void Duplicate_modifiers_are_rejected_before_input()
    {
        var sink = new RecordingKeyboardInputSink();

        var exception = Assert.Throws<ArgumentException>(() =>
            KeyboardInputEngine.SendSequence(
                [new KeyStroke(KeyboardKey.A, [KeyboardModifier.Control, KeyboardModifier.Control])],
                sink));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("repeats modifier 'Control'"));
            Assert.That(sink.Events, Is.Empty);
        });
    }

    [TestCase(0)]
    [TestCase(KeyboardInputEngine.MaximumSequenceLength + 1)]
    public void Sequence_length_is_bounded(int count)
    {
        var sequence = Enumerable.Range(0, count)
            .Select(_ => new KeyStroke(KeyboardKey.A))
            .ToArray();

        var exception = Assert.Throws<ArgumentException>(() =>
            KeyboardInputEngine.BuildSequence(sequence));

        Assert.That(exception!.Message, Does.Contain("between 1 and 100 steps"));
    }

    [Test]
    public void Modifier_press_failure_releases_every_modifier_that_was_pressed()
    {
        var sink = new RecordingKeyboardInputSink("press:SHIFT");

        var exception = Assert.Throws<TestKeyboardInputException>(() =>
            KeyboardInputEngine.SendSequence(
                [new KeyStroke(KeyboardKey.A, [KeyboardModifier.Control, KeyboardModifier.Shift])],
                sink));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Is.EqualTo("press:SHIFT"));
            Assert.That(sink.Events, Is.EqualTo(new[]
            {
                "press:CONTROL",
                "press:SHIFT",
                "release:SHIFT",
                "release:CONTROL"
            }));
        });
    }

    [Test]
    public void Key_press_failure_releases_the_key_and_every_pressed_modifier()
    {
        var sink = new RecordingKeyboardInputSink("press:KEY_A");

        var exception = Assert.Throws<TestKeyboardInputException>(() =>
            KeyboardInputEngine.SendSequence(
                [new KeyStroke(KeyboardKey.A, [KeyboardModifier.Control, KeyboardModifier.Shift])],
                sink));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Is.EqualTo("press:KEY_A"));
            Assert.That(sink.Events, Is.EqualTo(new[]
            {
                "press:CONTROL",
                "press:SHIFT",
                "press:KEY_A",
                "release:KEY_A",
                "release:SHIFT",
                "release:CONTROL"
            }));
        });
    }

    [Test]
    public void Key_release_failure_still_releases_all_modifiers()
    {
        var sink = new RecordingKeyboardInputSink("release:KEY_A");

        var exception = Assert.Throws<TestKeyboardInputException>(() =>
            KeyboardInputEngine.SendSequence(
                [new KeyStroke(KeyboardKey.A, [KeyboardModifier.Control, KeyboardModifier.Shift])],
                sink));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Is.EqualTo("release:KEY_A"));
            Assert.That(sink.Events, Is.EqualTo(new[]
            {
                "press:CONTROL",
                "press:SHIFT",
                "press:KEY_A",
                "release:KEY_A",
                "release:SHIFT",
                "release:CONTROL"
            }));
        });
    }

    [TestCase(TextEntryMode.Replace, new[]
    {
        "press:CONTROL", "press:KEY_A", "release:KEY_A", "release:CONTROL",
        "press:DELETE", "release:DELETE", "type:hello"
    })]
    [TestCase(TextEntryMode.Append, new[]
    {
        "press:CONTROL", "press:END", "release:END", "release:CONTROL", "type:hello"
    })]
    [TestCase(TextEntryMode.AtSelection, new[] { "type:hello" })]
    public void Text_entry_modes_emit_explicit_keyboard_intentions(
        TextEntryMode mode,
        string[] expectedEvents)
    {
        var sink = new RecordingKeyboardInputSink();

        KeyboardInputEngine.TypeText("hello", mode, sink);

        Assert.That(sink.Events, Is.EqualTo(expectedEvents));
    }

    private sealed class RecordingKeyboardInputSink(params string[] failures) : IKeyboardInputSink
    {
        private readonly HashSet<string> _failures = new(failures, StringComparer.Ordinal);

        internal List<string> Events { get; } = [];

        public void Press(VirtualKeyShort key) => Record($"press:{key}");

        public void Release(VirtualKeyShort key) => Record($"release:{key}");

        public void Type(string text) => Record($"type:{text}");

        private void Record(string operation)
        {
            Events.Add(operation);
            if (_failures.Contains(operation))
            {
                throw new TestKeyboardInputException(operation);
            }
        }
    }

    private sealed class TestKeyboardInputException(string message) : Exception(message);
}
