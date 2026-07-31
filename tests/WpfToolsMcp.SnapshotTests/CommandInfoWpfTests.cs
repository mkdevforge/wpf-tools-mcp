using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WpfToolsMcp.Agent;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[Category("Wpf")]
[Apartment(ApartmentState.STA)]
[NonParallelizable]
public sealed class CommandInfoWpfTests
{
    [Test]
    public void Routed_command_reports_ancestor_bindings_gestures_and_enabled_state_without_execution()
    {
        var ownerId = $"command-routed-{Guid.NewGuid():N}";
        var command = new RoutedCommand("Inspect", typeof(CommandInfoWpfTests));
        var parameter = new DisplayValue("payload");
        var customGesture = new ProbeGesture();
        var executed = 0;
        var panel = new StackPanel { Name = "CommandPanel" };
        var implicitTarget = CreateButton("ImplicitCommandTarget", command, parameter);
        implicitTarget.IsEnabled = false;
        var explicitTarget = CreateButton("ExplicitCommandTarget", command, "explicit");
        explicitTarget.CommandTarget = panel;
        panel.Children.Add(implicitTarget);
        panel.Children.Add(explicitTarget);
        panel.CommandBindings.Add(new CommandBinding(
            command,
            (_, _) => executed++,
            (_, args) => args.CanExecute = true));
        panel.InputBindings.Add(new KeyBinding(command, new KeyGesture(Key.K, ModifierKeys.Control, "Ctrl+K")));
        panel.InputBindings.Add(new MouseBinding(command, new MouseGesture(MouseAction.LeftDoubleClick, ModifierKeys.Alt)));
        panel.InputBindings.Add(new InputBinding(command, customGesture));
        var window = CreateWindow(panel);

        try
        {
            ShowAndLayout(window);
            var implicitResponse = Inspect(window, ownerId, "ImplicitCommandTarget");
            var explicitResponse = Inspect(window, ownerId, "ExplicitCommandTarget");
            var ancestor = implicitResponse.ContextChain.Single(context => context.Element.Name == "CommandPanel");
            var gestures = ancestor.InputBindings.Bindings.Select(binding => binding.Gesture).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(implicitResponse.Element.ElementIdWpf, Is.Not.Null.And.Not.Empty);
                Assert.That(implicitResponse.Source.Status, Is.EqualTo(CommandInspectionStatus.Available));
                Assert.That(implicitResponse.Source.Command!.Name, Is.EqualTo("Inspect"));
                Assert.That(implicitResponse.Source.Parameter.Formatted!.Value, Is.EqualTo("payload"));
                Assert.That(implicitResponse.Source.Parameter.Formatted.Evidence.Kind,
                    Is.EqualTo(ProvenanceEvidenceKind.BestEffort));
                Assert.That(implicitResponse.ControlIsEnabled.IsEnabled, Is.False);
                Assert.That(implicitResponse.CanExecute.Status, Is.EqualTo(CommandInspectionStatus.Available));
                Assert.That(implicitResponse.CanExecute.CanExecute, Is.True);
                Assert.That(implicitResponse.CanExecute.Mode, Is.EqualTo(CommandCanExecuteMode.RoutedCommand));
                Assert.That(implicitResponse.CanExecute.UsedCommandSourceFallback, Is.True);
                Assert.That(ancestor.Depth, Is.EqualTo(1));
                Assert.That(ancestor.CommandBindings.DiscoveredCount, Is.EqualTo(1));
                Assert.That(ancestor.CommandBindings.Bindings.Single().MatchesSourceCommand, Is.True);
                Assert.That(gestures.Select(gesture => gesture.Kind),
                    Is.EqualTo(new[] { CommandGestureKind.Key, CommandGestureKind.Mouse, CommandGestureKind.Custom }));
                Assert.That(gestures[0].Key, Is.EqualTo(nameof(Key.K)));
                Assert.That(gestures[0].Modifiers, Is.EqualTo(nameof(ModifierKeys.Control)));
                Assert.That(gestures[1].MouseAction, Is.EqualTo(nameof(MouseAction.LeftDoubleClick)));
                Assert.That(gestures[2].Status, Is.EqualTo(CommandInspectionStatus.Unsupported));
                Assert.That(explicitResponse.CanExecute.UsedCommandSourceFallback, Is.False);
                Assert.That(explicitResponse.CanExecute.EffectiveTarget!.Element!.Name, Is.EqualTo("CommandPanel"));
                Assert.That(explicitResponse.ContextChain[0].Element.Name, Is.EqualTo("CommandPanel"));
                Assert.That(executed, Is.Zero);
                Assert.That(customGesture.MatchesCalls, Is.Zero);
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Custom_commands_report_true_false_throwing_and_value_formatting_states()
    {
        var ownerId = $"command-custom-{Guid.NewGuid():N}";
        var trueCommand = new ProbeCommand(_ => true);
        var falseCommand = new ProbeCommand(_ => false);
        var throwingCommand = new ProbeCommand(_ => throw new CommandProbeException("CanExecute failed"));
        var throwingValue = new ThrowingStringifier();
        var panel = new StackPanel();
        panel.Children.Add(CreateButton("TrueCommand", trueCommand, "alpha"));
        panel.Children.Add(CreateButton("FalseCommand", falseCommand, throwingValue));
        var throwingSource = new CommandSourceElement(throwingCommand, 42)
        {
            Name = "ThrowingCommand",
            Width = 120,
            Height = 24
        };
        AutomationProperties.SetAutomationId(throwingSource, "ThrowingCommand");
        panel.Children.Add(throwingSource);
        var window = CreateWindow(panel);

        try
        {
            ShowAndLayout(window);
            var trueResponse = Inspect(window, ownerId, "TrueCommand", maxValueLength: 32);
            var falseResponse = Inspect(window, ownerId, "FalseCommand", maxValueLength: 32);
            var throwingResponse = Inspect(window, ownerId, "ThrowingCommand", maxValueLength: 32);

            Assert.Multiple(() =>
            {
                Assert.That(trueResponse.CanExecute.CanExecute, Is.True);
                Assert.That(falseResponse.CanExecute.CanExecute, Is.False);
                Assert.That(falseResponse.Source.Parameter.Status, Is.EqualTo(CommandInspectionStatus.Available));
                Assert.That(falseResponse.Source.Parameter.Formatted!.Evidence.Kind,
                    Is.EqualTo(ProvenanceEvidenceKind.Unavailable));
                Assert.That(falseResponse.Source.Parameter.Formatted.Evidence.Reason,
                    Does.StartWith("value_to_string_failed:"));
                Assert.That(throwingResponse.CanExecute.Status, Is.EqualTo(CommandInspectionStatus.Threw));
                Assert.That(throwingResponse.CanExecute.Failure!.Type, Is.EqualTo(typeof(CommandProbeException).FullName));
                Assert.That(throwingResponse.CanExecute.Failure.Message, Is.EqualTo("CanExecute failed"));
                Assert.That(trueCommand.ExecuteCalls, Is.Zero);
                Assert.That(falseCommand.ExecuteCalls, Is.Zero);
                Assert.That(throwingCommand.ExecuteCalls, Is.Zero);
                Assert.That(throwingValue.ToStringCalls, Is.EqualTo(1));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Missing_unsupported_and_throwing_command_sources_are_structured()
    {
        var ownerId = $"command-source-states-{Guid.NewGuid():N}";
        var panel = new StackPanel();
        panel.Children.Add(CreateButton("MissingCommand", command: null, parameter: null));
        var unsupported = new Border { Name = "UnsupportedCommand", Width = 20, Height = 20 };
        AutomationProperties.SetAutomationId(unsupported, "UnsupportedCommand");
        panel.Children.Add(unsupported);
        var throwing = new ThrowingCommandSource { Name = "ThrowingCommand", Width = 20, Height = 20 };
        AutomationProperties.SetAutomationId(throwing, "ThrowingCommand");
        panel.Children.Add(throwing);
        var window = CreateWindow(panel);

        try
        {
            ShowAndLayout(window);
            var missing = Inspect(window, ownerId, "MissingCommand");
            var unsupportedResponse = Inspect(window, ownerId, "UnsupportedCommand");
            var throwingResponse = Inspect(window, ownerId, "ThrowingCommand");

            Assert.Multiple(() =>
            {
                Assert.That(missing.Source.Status, Is.EqualTo(CommandInspectionStatus.Missing));
                Assert.That(missing.CanExecute.Status, Is.EqualTo(CommandInspectionStatus.NotEvaluated));
                Assert.That(missing.CanExecute.UnavailableReason, Is.EqualTo("command_missing"));
                Assert.That(unsupportedResponse.Source.Status, Is.EqualTo(CommandInspectionStatus.Unsupported));
                Assert.That(unsupportedResponse.ControlIsEnabled.Status, Is.EqualTo(CommandInspectionStatus.Available));
                Assert.That(throwingResponse.Source.Status, Is.EqualTo(CommandInspectionStatus.Threw));
                Assert.That(throwingResponse.Source.Parameter.Status, Is.EqualTo(CommandInspectionStatus.Threw));
                Assert.That(throwingResponse.Source.Target.Status, Is.EqualTo(CommandInspectionStatus.Threw));
                Assert.That(throwingResponse.Source.Failure!.Message, Does.Contain("command getter"));
                Assert.That(throwingResponse.CanExecute.Status, Is.EqualTo(CommandInspectionStatus.NotEvaluated));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Context_and_binding_caps_report_omitted_evidence()
    {
        var ownerId = $"command-bounds-{Guid.NewGuid():N}";
        var command = new ProbeCommand(_ => true);
        var button = CreateButton("BoundedCommand", command, null);
        button.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy));
        button.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste));
        button.InputBindings.Add(new KeyBinding(command, Key.F2, ModifierKeys.None));
        var window = CreateWindow(new Border { Child = button });

        try
        {
            ShowAndLayout(window);
            var response = Inspect(
                window,
                ownerId,
                "BoundedCommand",
                maxAncestors: 0,
                maxBindings: 1);

            Assert.Multiple(() =>
            {
                Assert.That(response.ContextChain, Has.Count.EqualTo(1));
                Assert.That(response.Counts.DiscoveredCommandBindings, Is.EqualTo(2));
                Assert.That(response.Counts.DiscoveredInputBindings, Is.EqualTo(1));
                Assert.That(response.Counts.ReturnedCommandBindings + response.Counts.ReturnedInputBindings,
                    Is.EqualTo(1));
                Assert.That(response.Truncated, Is.True);
                Assert.That(response.TruncatedReasons, Does.Contain("maxAncestors"));
                Assert.That(response.TruncatedReasons, Does.Contain("maxBindings"));
                Assert.That(command.ExecuteCalls, Is.Zero);
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Routed_command_with_no_input_target_still_uses_framework_focus_fallback()
    {
        var command = new RoutedCommand("FocusFallback", typeof(CommandInfoWpfTests));
        var sourceElement = new NonInputCommandSource(command);
        var source = new CommandSourceInfo(
            CommandInspectionStatus.Available,
            sourceElement.GetType().FullName!,
            "ICommandSource.Command",
            new CommandIdentityInfo(command.GetType().FullName!, command.Name, command.OwnerType.FullName),
            new CommandMemberValue(CommandInspectionStatus.Null),
            new CommandTargetInfo(CommandInspectionStatus.Null));

        var result = WpfVisualTreeInspector.EvaluateCanExecute(
            sourceElement,
            source,
            command,
            parameter: null,
            parameterAvailable: true,
            commandTarget: null,
            targetAvailable: true,
            maxValueLength: 100);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandInspectionStatus.Available));
            Assert.That(result.Mode, Is.EqualTo(CommandCanExecuteMode.RoutedCommand));
            Assert.That(result.CanExecute, Is.Not.Null);
            Assert.That(result.EffectiveTarget!.Status, Is.EqualTo(CommandInspectionStatus.Null));
            Assert.That(result.UsedCommandSourceFallback, Is.False);
            Assert.That(result.UnavailableReason, Is.Null);
        });
    }

    private static Button CreateButton(string automationId, ICommand? command, object? parameter)
    {
        var button = new Button
        {
            Name = automationId,
            Width = 120,
            Height = 24,
            Command = command,
            CommandParameter = parameter
        };
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private static GetCommandInfoResponse Inspect(
        Window window,
        string ownerId,
        string automationId,
        int maxAncestors = 8,
        int maxBindings = 128,
        int maxValueLength = 500) =>
        WpfVisualTreeInspector.GetCommandInfo(
            ownerId,
            new GetCommandInfoRequest(
                WindowHandle: GetWindowHandle(window),
                Locator: new ElementLocator(AutomationId: automationId),
                MaxAncestors: maxAncestors,
                MaxBindings: maxBindings,
                MaxValueLength: maxValueLength),
            CancellationToken.None);

    private static Window CreateWindow(UIElement content) =>
        new()
        {
            Title = "Command info WPF unit test",
            Width = 320,
            Height = 220,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Left = -10_000,
            Top = -10_000,
            Content = content
        };

    private static void ShowAndLayout(Window window)
    {
        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private static long GetWindowHandle(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle.ToInt64();
        Assert.That(handle, Is.Not.Zero);
        return handle;
    }

    private static void CloseAndRelease(Window window, string ownerId)
    {
        WpfVisualTreeInspector.ReleaseOwnerResources(ownerId);
        if (window.IsVisible)
        {
            window.Close();
        }
    }

    private sealed class ProbeCommand(Func<object?, bool> canExecute) : ICommand
    {
        public int ExecuteCalls { get; private set; }

        public bool CanExecute(object? parameter) => canExecute(parameter);

        public void Execute(object? parameter) => ExecuteCalls++;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class ProbeGesture : InputGesture
    {
        public int MatchesCalls { get; private set; }

        public override bool Matches(object targetElement, InputEventArgs inputEvent)
        {
            MatchesCalls++;
            throw new InvalidOperationException("Gesture matching must not run during inspection.");
        }
    }

    private sealed class DisplayValue(string text)
    {
        public override string ToString() => text;
    }

    private sealed class ThrowingStringifier
    {
        public int ToStringCalls { get; private set; }

        public override string ToString()
        {
            ToStringCalls++;
            throw new InvalidOperationException("Parameter formatting failed.");
        }
    }

    private sealed class ThrowingCommandSource : FrameworkElement, ICommandSource
    {
        public ICommand Command => throw new InvalidOperationException("Application command getter failed.");

        public object CommandParameter => throw new InvalidOperationException("Application parameter getter failed.");

        public IInputElement CommandTarget => throw new InvalidOperationException("Application target getter failed.");
    }

    private sealed class CommandSourceElement(
        ICommand command,
        object? parameter,
        IInputElement? target = null) : FrameworkElement, ICommandSource
    {
        public ICommand Command { get; } = command;

        public object? CommandParameter { get; } = parameter;

        public IInputElement? CommandTarget { get; } = target;
    }

    private sealed class NonInputCommandSource(ICommand command) : DependencyObject, ICommandSource
    {
        public ICommand Command { get; } = command;

        public object? CommandParameter => null;

        public IInputElement? CommandTarget => null;
    }

    private sealed class CommandProbeException(string message) : Exception(message);
}
