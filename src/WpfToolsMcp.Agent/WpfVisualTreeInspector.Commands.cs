using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Snoop.Data.Tree;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Agent;

internal static partial class WpfVisualTreeInspector
{
    private const int MaxCommandAncestors = 32;
    private const int MaxCommandBindings = 512;
    private const int MaxCommandValueLength = 2_000;
    private const int MaxCommandResolutionNodes = 200_000;

    public static GetCommandInfoResponse GetCommandInfo(
        string ownerId,
        GetCommandInfoRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        var maxAncestors = Math.Clamp(request.MaxAncestors, 0, MaxCommandAncestors);
        var maxBindings = Math.Clamp(request.MaxBindings, 0, MaxCommandBindings);
        var maxValueLength = Math.Clamp(request.MaxValueLength, 1, MaxCommandValueLength);

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();
        var resolved = ResolveTargetElement(
            ownerId,
            window,
            treeService,
            rootObject: window,
            rootXPath: "/Window",
            request.Locator,
            request.ElementId,
            request.WindowHandle,
            visibleOnly: false,
            includeOffViewport: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes: MaxCommandResolutionNodes,
            cancellationToken);

        var sourceElement = resolved.Element;
        var elementRef = BuildElementRefWpf(ownerId, sourceElement, resolved.XPath, FindReturnFields.Standard);
        var source = ReadCommandSource(sourceElement, maxValueLength, out var sourceCommand, out var parameter,
            out var parameterAvailable, out var commandTarget, out var targetAvailable);
        var enabled = ReadCommandEnabled(sourceElement, maxValueLength);
        var canExecute = EvaluateCanExecute(
            sourceElement,
            source,
            sourceCommand,
            parameter,
            parameterAvailable,
            commandTarget,
            targetAvailable,
            maxValueLength);

        var chainStart = sourceCommand is RoutedCommand && commandTarget is DependencyObject dependencyTarget
            ? dependencyTarget
            : sourceElement;
        var knownPaths = BuildKnownCommandPaths(
            treeService,
            window,
            chainStart,
            sourceElement,
            resolved.XPath,
            cancellationToken);
        var contexts = new List<CommandContextInfo>(maxAncestors + 1);
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance) { chainStart };
        var truncatedReasons = new List<string>(3);
        var remainingBindings = maxBindings;
        var discoveredCommandBindings = 0;
        var returnedCommandBindings = 0;
        var discoveredInputBindings = 0;
        var returnedInputBindings = 0;
        var parentChainStatus = CommandInspectionStatus.Available;
        DiagnosticCauseInfo? parentChainFailure = null;
        var current = chainStart;

        for (var depth = 0; ; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            knownPaths.TryGetValue(current, out var xpath);
            var commandBindings = InspectCommandBindings(
                current,
                sourceCommand,
                maxValueLength,
                ref remainingBindings,
                out var returnedCommands);
            var inputBindings = InspectInputBindings(
                current,
                sourceCommand,
                maxValueLength,
                ref remainingBindings,
                out var returnedInputs);

            discoveredCommandBindings = SaturatingAdd(discoveredCommandBindings, commandBindings.DiscoveredCount);
            returnedCommandBindings = SaturatingAdd(returnedCommandBindings, returnedCommands);
            discoveredInputBindings = SaturatingAdd(discoveredInputBindings, inputBindings.DiscoveredCount);
            returnedInputBindings = SaturatingAdd(returnedInputBindings, returnedInputs);
            contexts.Add(new CommandContextInfo(
                Depth: depth,
                Element: BuildCommandElementSummary(current, xpath),
                CommandBindings: commandBindings,
                InputBindings: inputBindings));

            if (ReferenceEquals(current, window))
            {
                break;
            }

            var parent = TryGetCommandParent(current, maxValueLength, out var parentFailure);
            if (parentFailure is not null)
            {
                parentChainStatus = CommandInspectionStatus.Threw;
                parentChainFailure = parentFailure;
                AddCommandTruncationReason(truncatedReasons, "parentInspectionUnavailable");
                break;
            }

            if (parent is null)
            {
                break;
            }

            if (depth >= maxAncestors)
            {
                AddCommandTruncationReason(truncatedReasons, "maxAncestors");
                break;
            }

            if (!visited.Add(parent))
            {
                parentChainStatus = CommandInspectionStatus.Unavailable;
                AddCommandTruncationReason(truncatedReasons, "parentCycle");
                break;
            }

            current = parent;
        }

        if (discoveredCommandBindings > returnedCommandBindings ||
            discoveredInputBindings > returnedInputBindings)
        {
            AddCommandTruncationReason(truncatedReasons, "maxBindings");
        }

        return new GetCommandInfoResponse(
            Element: elementRef,
            Source: source,
            ControlIsEnabled: enabled,
            CanExecute: canExecute,
            ContextChain: contexts,
            Counts: new CommandInspectionCounts(
                ReturnedContexts: contexts.Count,
                DiscoveredCommandBindings: discoveredCommandBindings,
                ReturnedCommandBindings: returnedCommandBindings,
                DiscoveredInputBindings: discoveredInputBindings,
                ReturnedInputBindings: returnedInputBindings),
            ParentChainStatus: parentChainStatus,
            Truncated: truncatedReasons.Count > 0,
            TruncatedReason: truncatedReasons.FirstOrDefault(),
            TruncatedReasons: truncatedReasons.Count > 0 ? truncatedReasons : null,
            ParentChainFailure: parentChainFailure)
        {
            WindowHandleUsed = GetWindowHandle(window)
        };
    }

    private static CommandSourceInfo ReadCommandSource(
        DependencyObject element,
        int maxValueLength,
        out ICommand? command,
        out object? parameter,
        out bool parameterAvailable,
        out IInputElement? target,
        out bool targetAvailable)
    {
        command = null;
        parameter = null;
        parameterAvailable = false;
        target = null;
        targetAvailable = false;
        var sourceType = BoundCommandText(element.GetType().FullName ?? element.GetType().Name, 512);

        if (element is not ICommandSource source)
        {
            return new CommandSourceInfo(
                Status: CommandInspectionStatus.Unsupported,
                SourceType: sourceType,
                CommandProperty: "ICommandSource.Command",
                Command: null,
                Parameter: new CommandMemberValue(CommandInspectionStatus.NotEvaluated),
                Target: new CommandTargetInfo(CommandInspectionStatus.NotEvaluated));
        }

        Exception? commandFailure = null;
        try
        {
            command = source.Command;
        }
        catch (Exception ex)
        {
            commandFailure = ex;
        }

        CommandMemberValue parameterInfo;
        try
        {
            parameter = source.CommandParameter;
            parameterAvailable = true;
            parameterInfo = BuildCommandMemberValue(parameter, maxValueLength);
        }
        catch (Exception ex)
        {
            parameterInfo = new CommandMemberValue(
                CommandInspectionStatus.Threw,
                Failure: BuildCommandCause(ex, maxValueLength));
        }

        CommandTargetInfo targetInfo;
        try
        {
            target = source.CommandTarget;
            targetAvailable = true;
            targetInfo = target is null
                ? new CommandTargetInfo(CommandInspectionStatus.Null)
                : BuildCommandTargetInfo(target);
        }
        catch (Exception ex)
        {
            targetInfo = new CommandTargetInfo(
                CommandInspectionStatus.Threw,
                Failure: BuildCommandCause(ex, maxValueLength));
        }

        var status = commandFailure is not null
            ? CommandInspectionStatus.Threw
            : command is null
                ? CommandInspectionStatus.Missing
                : CommandInspectionStatus.Available;
        return new CommandSourceInfo(
            Status: status,
            SourceType: sourceType,
            CommandProperty: "ICommandSource.Command",
            Command: command is null ? null : BuildCommandIdentity(command, maxValueLength),
            Parameter: parameterInfo,
            Target: targetInfo,
            Failure: commandFailure is null ? null : BuildCommandCause(commandFailure, maxValueLength));
    }

    private static CommandEnabledInfo ReadCommandEnabled(DependencyObject element, int maxValueLength)
    {
        try
        {
            return element switch
            {
                UIElement uiElement => new CommandEnabledInfo(CommandInspectionStatus.Available, uiElement.IsEnabled),
                ContentElement contentElement => new CommandEnabledInfo(CommandInspectionStatus.Available, contentElement.IsEnabled),
                UIElement3D uiElement3D => new CommandEnabledInfo(CommandInspectionStatus.Available, uiElement3D.IsEnabled),
                _ => new CommandEnabledInfo(CommandInspectionStatus.Unsupported)
            };
        }
        catch (Exception ex)
        {
            return new CommandEnabledInfo(
                CommandInspectionStatus.Threw,
                Failure: BuildCommandCause(ex, maxValueLength));
        }
    }

    internal static CommandCanExecuteInfo EvaluateCanExecute(
        DependencyObject sourceElement,
        CommandSourceInfo source,
        ICommand? command,
        object? parameter,
        bool parameterAvailable,
        IInputElement? commandTarget,
        bool targetAvailable,
        int maxValueLength)
    {
        if (source.Status == CommandInspectionStatus.Unsupported)
        {
            return new CommandCanExecuteInfo(
                CommandInspectionStatus.NotEvaluated,
                UnavailableReason: "unsupported_command_source");
        }

        if (source.Status == CommandInspectionStatus.Threw)
        {
            return new CommandCanExecuteInfo(
                CommandInspectionStatus.NotEvaluated,
                UnavailableReason: "command_getter_failed",
                Failure: source.Failure);
        }

        if (command is null)
        {
            return new CommandCanExecuteInfo(
                CommandInspectionStatus.NotEvaluated,
                UnavailableReason: "command_missing");
        }

        var mode = command is RoutedCommand
            ? CommandCanExecuteMode.RoutedCommand
            : CommandCanExecuteMode.Command;
        if (!parameterAvailable)
        {
            return new CommandCanExecuteInfo(
                CommandInspectionStatus.NotEvaluated,
                Mode: mode,
                UnavailableReason: "command_parameter_getter_failed",
                Failure: source.Parameter.Failure);
        }

        if (command is not RoutedCommand routedCommand)
        {
            try
            {
                return new CommandCanExecuteInfo(
                    CommandInspectionStatus.Available,
                    CanExecute: command.CanExecute(parameter),
                    Mode: mode);
            }
            catch (Exception ex)
            {
                return new CommandCanExecuteInfo(
                    CommandInspectionStatus.Threw,
                    Mode: mode,
                    Failure: BuildCommandCause(ex, maxValueLength));
            }
        }

        if (!targetAvailable)
        {
            return new CommandCanExecuteInfo(
                CommandInspectionStatus.NotEvaluated,
                Mode: mode,
                UnavailableReason: "command_target_getter_failed",
                Failure: source.Target.Failure);
        }

        var effectiveTarget = commandTarget ?? sourceElement as IInputElement;
        var usedSourceFallback = commandTarget is null && effectiveTarget is not null;
        var effectiveTargetInfo = effectiveTarget is null
            ? new CommandTargetInfo(CommandInspectionStatus.Null)
            : BuildCommandTargetInfo(effectiveTarget);
        try
        {
            return new CommandCanExecuteInfo(
                CommandInspectionStatus.Available,
                CanExecute: routedCommand.CanExecute(parameter, effectiveTarget),
                Mode: mode,
                EffectiveTarget: effectiveTargetInfo,
                UsedCommandSourceFallback: usedSourceFallback);
        }
        catch (Exception ex)
        {
            return new CommandCanExecuteInfo(
                CommandInspectionStatus.Threw,
                Mode: mode,
                EffectiveTarget: effectiveTargetInfo,
                UsedCommandSourceFallback: usedSourceFallback,
                Failure: BuildCommandCause(ex, maxValueLength));
        }
    }

    private static CommandBindingCollectionInfo InspectCommandBindings(
        DependencyObject element,
        ICommand? sourceCommand,
        int maxValueLength,
        ref int remainingBindings,
        out int returnedCount)
    {
        returnedCount = 0;
        try
        {
            if (!ShouldSerializeCommandBindings(element))
            {
                return new CommandBindingCollectionInfo(CommandInspectionStatus.Empty, 0, []);
            }

            var bindings = GetCommandBindings(element);
            if (bindings is null || bindings.Count == 0)
            {
                return new CommandBindingCollectionInfo(CommandInspectionStatus.Empty, 0, []);
            }

            var discoveredCount = bindings.Count;
            var returned = new List<CommandBindingInspection>(Math.Min(discoveredCount, remainingBindings));
            for (var index = 0; index < discoveredCount && remainingBindings > 0; index++)
            {
                CommandBinding binding;
                try
                {
                    binding = bindings[index];
                }
                catch (Exception ex)
                {
                    returned.Add(new CommandBindingInspection(
                        index,
                        CommandInspectionStatus.Threw,
                        Failure: BuildCommandCause(ex, maxValueLength)));
                    remainingBindings--;
                    continue;
                }

                try
                {
                    var command = binding.Command;
                    returned.Add(new CommandBindingInspection(
                        index,
                        command is null ? CommandInspectionStatus.Null : CommandInspectionStatus.Available,
                        command is null ? null : BuildCommandIdentity(command, maxValueLength),
                        sourceCommand is null ? null : ReferenceEquals(sourceCommand, command)));
                }
                catch (Exception ex)
                {
                    returned.Add(new CommandBindingInspection(
                        index,
                        CommandInspectionStatus.Threw,
                        Failure: BuildCommandCause(ex, maxValueLength)));
                }

                remainingBindings--;
            }

            returnedCount = returned.Count;
            return new CommandBindingCollectionInfo(
                CommandInspectionStatus.Available,
                discoveredCount,
                returned);
        }
        catch (Exception ex)
        {
            return new CommandBindingCollectionInfo(
                CommandInspectionStatus.Threw,
                0,
                [],
                BuildCommandCause(ex, maxValueLength));
        }
    }

    private static InputBindingCollectionInfo InspectInputBindings(
        DependencyObject element,
        ICommand? sourceCommand,
        int maxValueLength,
        ref int remainingBindings,
        out int returnedCount)
    {
        returnedCount = 0;
        try
        {
            if (!ShouldSerializeInputBindings(element))
            {
                return new InputBindingCollectionInfo(CommandInspectionStatus.Empty, 0, []);
            }

            var bindings = GetInputBindings(element);
            if (bindings is null || bindings.Count == 0)
            {
                return new InputBindingCollectionInfo(CommandInspectionStatus.Empty, 0, []);
            }

            var discoveredCount = bindings.Count;
            var returned = new List<InputBindingInspection>(Math.Min(discoveredCount, remainingBindings));
            for (var index = 0; index < discoveredCount && remainingBindings > 0; index++)
            {
                InputBinding binding;
                try
                {
                    binding = bindings[index];
                }
                catch (Exception ex)
                {
                    returned.Add(new InputBindingInspection(
                        index,
                        Type: "Unavailable",
                        Status: CommandInspectionStatus.Threw,
                        Command: null,
                        MatchesSourceCommand: null,
                        Parameter: new CommandMemberValue(CommandInspectionStatus.NotEvaluated),
                        Gesture: new CommandGestureInfo(CommandInspectionStatus.NotEvaluated),
                        Failure: BuildCommandCause(ex, maxValueLength)));
                    remainingBindings--;
                    continue;
                }

                returned.Add(BuildInputBindingInspection(index, binding, sourceCommand, maxValueLength));
                remainingBindings--;
            }

            returnedCount = returned.Count;
            return new InputBindingCollectionInfo(
                CommandInspectionStatus.Available,
                discoveredCount,
                returned);
        }
        catch (Exception ex)
        {
            return new InputBindingCollectionInfo(
                CommandInspectionStatus.Threw,
                0,
                [],
                BuildCommandCause(ex, maxValueLength));
        }
    }

    private static InputBindingInspection BuildInputBindingInspection(
        int index,
        InputBinding binding,
        ICommand? sourceCommand,
        int maxValueLength)
    {
        ICommand? command = null;
        DiagnosticCauseInfo? commandFailure = null;
        try
        {
            command = binding.Command;
        }
        catch (Exception ex)
        {
            commandFailure = BuildCommandCause(ex, maxValueLength);
        }

        CommandMemberValue parameter;
        try
        {
            parameter = BuildCommandMemberValue(binding.CommandParameter, maxValueLength);
        }
        catch (Exception ex)
        {
            parameter = new CommandMemberValue(
                CommandInspectionStatus.Threw,
                Failure: BuildCommandCause(ex, maxValueLength));
        }

        CommandGestureInfo gesture;
        try
        {
            gesture = BuildCommandGesture(binding.Gesture, maxValueLength);
        }
        catch (Exception ex)
        {
            gesture = new CommandGestureInfo(
                CommandInspectionStatus.Threw,
                Failure: BuildCommandCause(ex, maxValueLength));
        }

        var status = commandFailure is not null ||
                     parameter.Status == CommandInspectionStatus.Threw ||
                     gesture.Status == CommandInspectionStatus.Threw
            ? CommandInspectionStatus.Threw
            : command is null
                ? CommandInspectionStatus.Null
                : CommandInspectionStatus.Available;
        return new InputBindingInspection(
            Index: index,
            Type: BoundCommandText(binding.GetType().FullName ?? binding.GetType().Name, 512),
            Status: status,
            Command: command is null ? null : BuildCommandIdentity(command, maxValueLength),
            MatchesSourceCommand: sourceCommand is null ? null : ReferenceEquals(sourceCommand, command),
            Parameter: parameter,
            Gesture: gesture,
            Failure: commandFailure);
    }

    private static CommandGestureInfo BuildCommandGesture(InputGesture? gesture, int maxValueLength)
    {
        if (gesture is null)
        {
            return new CommandGestureInfo(CommandInspectionStatus.Null);
        }

        var type = BoundCommandText(gesture.GetType().FullName ?? gesture.GetType().Name, 512);
        return gesture switch
        {
            KeyGesture keyGesture => new CommandGestureInfo(
                CommandInspectionStatus.Available,
                Kind: CommandGestureKind.Key,
                Type: type,
                Key: keyGesture.Key.ToString(),
                Modifiers: keyGesture.Modifiers.ToString(),
                Display: BuildCommandMemberValue(keyGesture.DisplayString, maxValueLength)),
            MouseGesture mouseGesture => new CommandGestureInfo(
                CommandInspectionStatus.Available,
                Kind: CommandGestureKind.Mouse,
                Type: type,
                Modifiers: mouseGesture.Modifiers.ToString(),
                MouseAction: mouseGesture.MouseAction.ToString()),
            _ => new CommandGestureInfo(
                CommandInspectionStatus.Unsupported,
                Kind: CommandGestureKind.Custom,
                Type: type,
                Display: BuildCommandMemberValue(gesture, maxValueLength))
        };
    }

    private static CommandIdentityInfo BuildCommandIdentity(ICommand command, int maxValueLength)
    {
        var type = BoundCommandText(command.GetType().FullName ?? command.GetType().Name, 512);
        if (command is RoutedUICommand routedUiCommand)
        {
            return new CommandIdentityInfo(
                Type: type,
                Name: routedUiCommand.Name,
                OwnerType: routedUiCommand.OwnerType?.FullName,
                Text: BuildCommandMemberValue(routedUiCommand.Text, maxValueLength));
        }

        if (command is RoutedCommand routedCommand)
        {
            return new CommandIdentityInfo(
                Type: type,
                Name: routedCommand.Name,
                OwnerType: routedCommand.OwnerType?.FullName);
        }

        return new CommandIdentityInfo(
            Type: type,
            Display: BuildCommandMemberValue(command, maxValueLength));
    }

    private static CommandMemberValue BuildCommandMemberValue(object? value, int maxValueLength)
    {
        if (value is null)
        {
            return new CommandMemberValue(CommandInspectionStatus.Null);
        }

        var (formatted, evidence) = FormatProvenanceValueWithEvidence(value, "string", maxValueLength);
        return new CommandMemberValue(
            CommandInspectionStatus.Available,
            new CommandFormattedValue(
                Type: BoundCommandText(value.GetType().FullName ?? value.GetType().Name, 512),
                Value: formatted,
                Evidence: evidence,
                Truncated: string.Equals(evidence.Reason, "maxStringLength", StringComparison.Ordinal)));
    }

    private static CommandTargetInfo BuildCommandTargetInfo(IInputElement target)
    {
        var type = BoundCommandText(target.GetType().FullName ?? target.GetType().Name, 512);
        return target is DependencyObject dependencyObject
            ? new CommandTargetInfo(
                CommandInspectionStatus.Available,
                Type: type,
                Element: BuildCommandElementSummary(dependencyObject, xpath: null))
            : new CommandTargetInfo(CommandInspectionStatus.Available, Type: type);
    }

    private static CommandElementSummary BuildCommandElementSummary(DependencyObject element, string? xpath) =>
        new(
            Type: BoundCommandText(element.GetType().Name, 512),
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            ClassName: BoundCommandText(element.GetType().FullName ?? element.GetType().Name, 512),
            XPath: xpath);

    private static Dictionary<DependencyObject, string> BuildKnownCommandPaths(
        VisualTreeService treeService,
        Window window,
        DependencyObject chainStart,
        DependencyObject sourceElement,
        string sourceXPath,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<DependencyObject, string>(ReferenceEqualityComparer.Instance)
        {
            [sourceElement] = sourceXPath,
            [window] = "/Window"
        };

        try
        {
            foreach (var (element, xpath) in BuildXPathChainForElement(
                         treeService,
                         window,
                         chainStart,
                         visibleOnly: false,
                         maxNodes: MaxCommandResolutionNodes,
                         cancellationToken))
            {
                result[element] = xpath;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The public parent chain remains useful when an XPath cannot be reconstructed.
        }

        return result;
    }

    private static DependencyObject? TryGetCommandParent(
        DependencyObject element,
        int maxValueLength,
        out DiagnosticCauseInfo? failure)
    {
        failure = null;
        Exception? firstFailure = null;

        if (element is Visual or Visual3D)
        {
            try
            {
                var visualParent = VisualTreeHelper.GetParent(element);
                if (visualParent is not null)
                {
                    return visualParent;
                }
            }
            catch (Exception ex)
            {
                firstFailure = ex;
            }
        }

        if (element is ContentElement contentElement)
        {
            try
            {
                var contentParent = ContentOperations.GetParent(contentElement);
                if (contentParent is not null)
                {
                    return contentParent;
                }
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
        }

        try
        {
            var logicalParent = element switch
            {
                FrameworkElement frameworkElement => frameworkElement.Parent,
                FrameworkContentElement frameworkContentElement => frameworkContentElement.Parent,
                _ => LogicalTreeHelper.GetParent(element)
            };
            if (logicalParent is not null)
            {
                return logicalParent;
            }
        }
        catch (Exception ex)
        {
            firstFailure ??= ex;
        }

        if (firstFailure is not null)
        {
            failure = BuildCommandCause(firstFailure, maxValueLength);
        }

        return null;
    }

    private static bool ShouldSerializeCommandBindings(DependencyObject element) => element switch
    {
        UIElement uiElement => uiElement.ShouldSerializeCommandBindings(),
        ContentElement contentElement => contentElement.ShouldSerializeCommandBindings(),
        UIElement3D uiElement3D => uiElement3D.ShouldSerializeCommandBindings(),
        _ => false
    };

    private static CommandBindingCollection? GetCommandBindings(DependencyObject element) => element switch
    {
        UIElement uiElement => uiElement.CommandBindings,
        ContentElement contentElement => contentElement.CommandBindings,
        UIElement3D uiElement3D => uiElement3D.CommandBindings,
        _ => null
    };

    private static bool ShouldSerializeInputBindings(DependencyObject element) => element switch
    {
        UIElement uiElement => uiElement.ShouldSerializeInputBindings(),
        ContentElement contentElement => contentElement.ShouldSerializeInputBindings(),
        UIElement3D uiElement3D => uiElement3D.ShouldSerializeInputBindings(),
        _ => false
    };

    private static InputBindingCollection? GetInputBindings(DependencyObject element) => element switch
    {
        UIElement uiElement => uiElement.InputBindings,
        ContentElement contentElement => contentElement.InputBindings,
        UIElement3D uiElement3D => uiElement3D.InputBindings,
        _ => null
    };

    private static DiagnosticCauseInfo BuildCommandCause(Exception exception, int maxValueLength)
    {
        _ = TryFormatExceptionMessage(
            exception,
            Math.Clamp(maxValueLength, 1, MaxCommandValueLength),
            out var message,
            out _,
            out var messageUnavailableReason);
        return new DiagnosticCauseInfo(
            BoundCommandText(exception.GetType().FullName ?? exception.GetType().Name, 512))
        {
            Message = message,
            MessageUnavailableReason = messageUnavailableReason
        };
    }

    private static string BoundCommandText(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength];

    private static void AddCommandTruncationReason(List<string> reasons, string reason)
    {
        if (!reasons.Contains(reason, StringComparer.Ordinal))
        {
            reasons.Add(reason);
        }
    }
}
