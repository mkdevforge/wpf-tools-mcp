using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using WpfToolsMcp.Agent;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[Apartment(ApartmentState.STA)]
public sealed class WpfKeyboardAgentTests
{
    [Test]
    public void Current_agent_advertises_text_modes_and_element_focus()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                AgentProtocolCapabilities.Current,
                Does.Contain(AgentProtocolCapabilities.SetValueTextModes));
            Assert.That(
                AgentProtocolCapabilities.Current,
                Does.Contain(AgentProtocolCapabilities.FocusElement));
        });
    }

    [Test]
    public void Legacy_set_value_wire_shape_defaults_to_replace_without_emitting_text_mode()
    {
        var request = new SetWpfValueRequest(Text: "replacement");
        var json = JsonSerializer.Serialize(request);
        var roundTrip = JsonSerializer.Deserialize<SetWpfValueRequest>(json);

        Assert.Multiple(() =>
        {
            Assert.That(request.TextMode, Is.EqualTo(TextEntryMode.Replace));
            Assert.That(json, Does.Not.Contain("TextMode"));
            Assert.That(roundTrip!.TextMode, Is.EqualTo(TextEntryMode.Replace));
        });
    }

    [Test]
    public void Text_box_supports_replace_append_and_current_selection()
    {
        var textBox = new TextBox { Text = "alpha beta" };

        var replace = WpfVisualTreeInspector.TrySetWpfTextValue(
            textBox,
            "replacement",
            TextEntryMode.Replace);
        Assert.Multiple(() =>
        {
            Assert.That(textBox.Text, Is.EqualTo("replacement"));
            Assert.That(replace!.MethodUsed, Is.EqualTo("wpf_textBoxText"));
        });

        var append = WpfVisualTreeInspector.TrySetWpfTextValue(
            textBox,
            " tail",
            TextEntryMode.Append);
        Assert.Multiple(() =>
        {
            Assert.That(textBox.Text, Is.EqualTo("replacement tail"));
            Assert.That(append!.MethodUsed, Is.EqualTo("wpf_textBoxAppendText"));
        });

        textBox.Text = "alpha beta";
        textBox.Select(start: 6, length: 4);
        var atSelection = WpfVisualTreeInspector.TrySetWpfTextValue(
            textBox,
            "selected",
            TextEntryMode.AtSelection);
        Assert.Multiple(() =>
        {
            Assert.That(textBox.Text, Is.EqualTo("alpha selected"));
            Assert.That(textBox.SelectionStart, Is.EqualTo("alpha selected".Length));
            Assert.That(textBox.SelectionLength, Is.Zero);
            Assert.That(textBox.CaretIndex, Is.EqualTo("alpha selected".Length));
            Assert.That(atSelection!.MethodUsed, Is.EqualTo("wpf_textBoxSelectedText"));
        });
    }

    [Test]
    public void Password_box_supports_replace_and_append_but_not_selection_insertion()
    {
        var passwordBox = new PasswordBox { Password = "initial" };

        var replace = WpfVisualTreeInspector.TrySetWpfTextValue(
            passwordBox,
            "replacement",
            TextEntryMode.Replace);
        var append = WpfVisualTreeInspector.TrySetWpfTextValue(
            passwordBox,
            "-tail",
            TextEntryMode.Append);
        var atSelection = WpfVisualTreeInspector.TrySetWpfTextValue(
            passwordBox,
            "not-applied",
            TextEntryMode.AtSelection);

        Assert.Multiple(() =>
        {
            Assert.That(passwordBox.Password, Is.EqualTo("replacement-tail"));
            Assert.That(replace!.MethodUsed, Is.EqualTo("wpf_passwordBoxPassword"));
            Assert.That(append!.MethodUsed, Is.EqualTo("wpf_passwordBoxPasswordAppend"));
            Assert.That(atSelection, Is.Null);
        });
    }

    [Test]
    public void Numeric_set_value_keeps_replacing_text_based_wpf_targets()
    {
        var textBox = new TextBox { Text = "initial" };

        var response = WpfVisualTreeInspector.TrySetWpfTextTargetValue(
            textBox,
            "42.5",
            isTextInput: false,
            requestedTextMode: TextEntryMode.Replace);

        Assert.Multiple(() =>
        {
            Assert.That(textBox.Text, Is.EqualTo("42.5"));
            Assert.That(response!.MethodUsed, Is.EqualTo("wpf_textBoxText"));
        });
    }

    [Test]
    public void Editable_combo_box_uses_its_text_editor_for_selection_insertion()
    {
        const string xaml = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type ComboBox}">
                <TextBox x:Name="PART_EditableTextBox"
                         Text="{Binding Text, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
            </ControlTemplate>
            """;
        var template = (ControlTemplate)XamlReader.Parse(xaml);
        var comboBox = new ComboBox
        {
            IsEditable = true,
            Template = template,
            Text = "alpha beta"
        };
        comboBox.Measure(new Size(300, 40));
        comboBox.Arrange(new System.Windows.Rect(0, 0, 300, 40));
        _ = comboBox.ApplyTemplate();
        var editor = (TextBox)template.FindName("PART_EditableTextBox", comboBox);
        editor.Select(start: 6, length: 4);

        var response = WpfVisualTreeInspector.TrySetWpfTextValue(
            comboBox,
            "selected",
            TextEntryMode.AtSelection);

        Assert.Multiple(() =>
        {
            Assert.That(editor.Text, Is.EqualTo("alpha selected"));
            Assert.That(editor.SelectionStart, Is.EqualTo("alpha selected".Length));
            Assert.That(editor.SelectionLength, Is.Zero);
            Assert.That(editor.CaretIndex, Is.EqualTo("alpha selected".Length));
            Assert.That(comboBox.Text, Is.EqualTo("alpha selected"));
            Assert.That(response!.MethodUsed, Is.EqualTo("wpf_comboBoxSelectedText"));
        });
    }

    [Test]
    public void Non_focusable_wpf_target_fails_without_changing_keyboard_focus()
    {
        var focusedBefore = Keyboard.FocusedElement;
        var target = new Border { Focusable = false };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WpfVisualTreeInspector.FocusWpfElement(target));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.StartWith("focus_unsupported_wpf_target:"));
            Assert.That(Keyboard.FocusedElement, Is.SameAs(focusedBefore));
        });
    }
}
