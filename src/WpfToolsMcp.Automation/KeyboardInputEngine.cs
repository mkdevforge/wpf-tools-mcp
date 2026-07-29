using System.Runtime.ExceptionServices;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal interface IKeyboardInputSink
{
    void Press(VirtualKeyShort key);

    void Release(VirtualKeyShort key);

    void Type(string text);
}

internal sealed class FlaUiKeyboardInputSink : IKeyboardInputSink
{
    internal static FlaUiKeyboardInputSink Instance { get; } = new();

    private FlaUiKeyboardInputSink()
    {
    }

    public void Press(VirtualKeyShort key) => Keyboard.Press(key);

    public void Release(VirtualKeyShort key) => Keyboard.Release(key);

    public void Type(string text) => Keyboard.Type(text);
}

internal readonly record struct KeyboardInputChord(
    VirtualKeyShort Key,
    IReadOnlyList<VirtualKeyShort> Modifiers);

internal static class KeyboardInputEngine
{
    internal const int MaximumSequenceLength = 100;

    internal static TextEntryMode ResolveTextEntryMode(TextEntryMode? requestedMode, bool hasTarget) =>
        requestedMode switch
        {
            null => hasTarget ? TextEntryMode.Replace : TextEntryMode.AtSelection,
            TextEntryMode.Replace => TextEntryMode.Replace,
            TextEntryMode.Append => TextEntryMode.Append,
            TextEntryMode.AtSelection => TextEntryMode.AtSelection,
            _ => throw new ArgumentOutOfRangeException(
                nameof(requestedMode),
                requestedMode,
                "Unknown text entry mode.")
        };

    internal static bool CanUseSemanticValuePattern(TextEntryMode mode, bool isPassword) =>
        mode != TextEntryMode.AtSelection &&
        (mode != TextEntryMode.Append || !isPassword);

    internal static bool CanReadValuePatternText(bool isPassword) => !isPassword;

    internal static void TypeText(string text, TextEntryMode mode)
    {
        TypeText(text, mode, FlaUiKeyboardInputSink.Instance);
        Wait.UntilInputIsProcessed();
    }

    internal static void TypeText(string text, TextEntryMode mode, IKeyboardInputSink sink)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sink);

        switch (mode)
        {
            case TextEntryMode.Replace:
                SendChord(
                    new KeyboardInputChord(VirtualKeyShort.KEY_A, [VirtualKeyShort.CONTROL]),
                    sink);
                SendChord(new KeyboardInputChord(VirtualKeyShort.DELETE, []), sink);
                break;
            case TextEntryMode.Append:
                SendChord(
                    new KeyboardInputChord(VirtualKeyShort.END, [VirtualKeyShort.CONTROL]),
                    sink);
                break;
            case TextEntryMode.AtSelection:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown text entry mode.");
        }

        sink.Type(text);
    }

    internal static void SendSequence(
        IReadOnlyList<KeyStroke> sequence,
        CancellationToken cancellationToken = default)
    {
        var preparedSequence = BuildSequence(sequence);
        SendPreparedSequence(preparedSequence, cancellationToken);
    }

    internal static void SendSequence(
        IReadOnlyList<KeyStroke> sequence,
        IKeyboardInputSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var chords = BuildSequence(sequence);
        SendPreparedSequence(chords, sink, cancellationToken);
    }

    internal static void SendPreparedSequence(
        IReadOnlyList<KeyboardInputChord> sequence,
        CancellationToken cancellationToken = default)
    {
        SendPreparedSequence(sequence, FlaUiKeyboardInputSink.Instance, cancellationToken);
        Wait.UntilInputIsProcessed();
    }

    internal static void SendPreparedSequence(
        IReadOnlyList<KeyboardInputChord> sequence,
        IKeyboardInputSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(sink);
        foreach (var chord in sequence)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendChord(chord, sink);
        }
    }

    internal static IReadOnlyList<KeyboardInputChord> BuildSequence(IReadOnlyList<KeyStroke> sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (sequence.Count is < 1 or > MaximumSequenceLength)
        {
            throw new ArgumentException(
                $"invalid_request: send_keys sequence must contain between 1 and {MaximumSequenceLength} steps.",
                nameof(sequence));
        }

        var chords = new KeyboardInputChord[sequence.Count];
        for (var index = 0; index < sequence.Count; index++)
        {
            var stroke = sequence[index] ?? throw new ArgumentException(
                $"invalid_request: send_keys sequence step {index} cannot be null.",
                nameof(sequence));
            var modifiers = stroke.Modifiers;
            if (modifiers is null || modifiers.Count == 0)
            {
                chords[index] = new KeyboardInputChord(ToVirtualKey(stroke.Key), []);
                continue;
            }

            var seenModifiers = new HashSet<KeyboardModifier>();
            var mappedModifiers = new VirtualKeyShort[modifiers.Count];
            for (var modifierIndex = 0; modifierIndex < modifiers.Count; modifierIndex++)
            {
                var modifier = modifiers[modifierIndex];
                if (!seenModifiers.Add(modifier))
                {
                    throw new ArgumentException(
                        $"invalid_request: send_keys sequence step {index} repeats modifier '{modifier}'.",
                        nameof(sequence));
                }

                mappedModifiers[modifierIndex] = ToVirtualKey(modifier);
            }

            chords[index] = new KeyboardInputChord(ToVirtualKey(stroke.Key), mappedModifiers);
        }

        return chords;
    }

    internal static VirtualKeyShort ToVirtualKey(KeyboardKey key)
    {
        if (key is >= KeyboardKey.Digit0 and <= KeyboardKey.Digit9)
        {
            return (VirtualKeyShort)(
                (int)VirtualKeyShort.KEY_0 + (int)key - (int)KeyboardKey.Digit0);
        }

        if (key is >= KeyboardKey.A and <= KeyboardKey.Z)
        {
            return (VirtualKeyShort)(
                (int)VirtualKeyShort.KEY_A + (int)key - (int)KeyboardKey.A);
        }

        if (key is >= KeyboardKey.F1 and <= KeyboardKey.F24)
        {
            return (VirtualKeyShort)(
                (int)VirtualKeyShort.F1 + (int)key - (int)KeyboardKey.F1);
        }

        return key switch
        {
            KeyboardKey.Backspace => VirtualKeyShort.BACK,
            KeyboardKey.Tab => VirtualKeyShort.TAB,
            KeyboardKey.Enter => VirtualKeyShort.RETURN,
            KeyboardKey.Escape => VirtualKeyShort.ESCAPE,
            KeyboardKey.Space => VirtualKeyShort.SPACE,
            KeyboardKey.PageUp => VirtualKeyShort.PRIOR,
            KeyboardKey.PageDown => VirtualKeyShort.NEXT,
            KeyboardKey.End => VirtualKeyShort.END,
            KeyboardKey.Home => VirtualKeyShort.HOME,
            KeyboardKey.ArrowLeft => VirtualKeyShort.LEFT,
            KeyboardKey.ArrowUp => VirtualKeyShort.UP,
            KeyboardKey.ArrowRight => VirtualKeyShort.RIGHT,
            KeyboardKey.ArrowDown => VirtualKeyShort.DOWN,
            KeyboardKey.Insert => VirtualKeyShort.INSERT,
            KeyboardKey.Delete => VirtualKeyShort.DELETE,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown keyboard key.")
        };
    }

    internal static VirtualKeyShort ToVirtualKey(KeyboardModifier modifier) => modifier switch
    {
        KeyboardModifier.Shift => VirtualKeyShort.SHIFT,
        KeyboardModifier.Control => VirtualKeyShort.CONTROL,
        KeyboardModifier.Alt => VirtualKeyShort.ALT,
        KeyboardModifier.Windows => VirtualKeyShort.LWIN,
        _ => throw new ArgumentOutOfRangeException(nameof(modifier), modifier, "Unknown keyboard modifier.")
    };

    private static void SendChord(KeyboardInputChord chord, IKeyboardInputSink sink)
    {
        var pressedKeys = new List<VirtualKeyShort>(chord.Modifiers.Count + 1);
        Exception? failure = null;
        try
        {
            foreach (var modifier in chord.Modifiers)
            {
                pressedKeys.Add(modifier);
                sink.Press(modifier);
            }

            pressedKeys.Add(chord.Key);
            sink.Press(chord.Key);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            for (var index = pressedKeys.Count - 1; index >= 0; index--)
            {
                try
                {
                    sink.Release(pressedKeys[index]);
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
