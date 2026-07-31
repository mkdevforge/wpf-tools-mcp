using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using Snoop.Data.Tree;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Agent;

internal static partial class WpfVisualTreeInspector
{
    private const int MaxValidationDepth = 100;
    private const int MaxValidationWarnings = 20;
    private const int MaxValidationWarningLength = 1000;
    private const int MaxValidationRootXPathLength = 2000;
    private const int MaxValidationPropertyNameLength = 512;
    private const int MaxValidationStatusLength = 128;

    public static GetValidationErrorsResponse GetValidationErrors(
        string ownerId,
        GetValidationErrorsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        var requestedDepth = request.Depth <= 0 ? 1 : request.Depth;
        var depth = Math.Clamp(requestedDepth, 1, MaxValidationDepth);
        var maxErrors = Math.Clamp(request.MaxErrors, 1, 1000);
        var maxNodes = Math.Clamp(request.MaxNodes, 1, 200_000);
        var maxValueLength = Math.Clamp(request.MaxValueLength, 1, 2000);

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();

        var rootObject = (DependencyObject)window;
        var rootXPath = "/Window";
        if (!string.IsNullOrWhiteSpace(request.RootXPath))
        {
            rootXPath = NormalizeXPath(request.RootXPath);
            rootObject = ResolveByXPath(
                treeService,
                window,
                rootXPath,
                request.VisibleOnly,
                cancellationToken);
        }

        var errors = new List<WpfValidationErrorInfo>();
        var warnings = new List<string>();
        var warningSet = new HashSet<string>(StringComparer.Ordinal);
        var discoveredWarnings = 0;
        var warningTextTruncated = false;
        var discoveredErrors = 0;
        var scannedNodes = 0;
        var scanComplete = true;
        var errorBudgetExceeded = false;
        var nodeBudgetExceeded = false;
        var depthBudgetExceeded = requestedDepth > MaxValidationDepth;
        if (depthBudgetExceeded)
        {
            scanComplete = false;
        }

        var stack = new Stack<(DependencyObject Element, string XPath, int RemainingDepth)>();
        stack.Push((rootObject, rootXPath, depth));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (scannedNodes >= maxNodes)
            {
                scanComplete = false;
                nodeBudgetExceeded = true;
                break;
            }

            var (current, currentXPath, remainingDepth) = stack.Pop();
            scannedNodes++;

            if (!ReferenceEquals(current, rootObject) && request.VisibleOnly && !IsVisibleWpf(current))
            {
                continue;
            }

            IReadOnlyList<ValidationError>? currentErrors = null;
            try
            {
                currentErrors = Validation.GetErrors(current);
            }
            catch (Exception ex)
            {
                scanComplete = false;
                AddValidationWarning(
                    warnings,
                    warningSet,
                    ref discoveredWarnings,
                    ref warningTextTruncated,
                    $"validation_errors_unavailable:{currentXPath}:{ex.GetType().Name}");
            }

            if (currentErrors is not null && currentErrors.Count > 0)
            {
                discoveredErrors += currentErrors.Count;
                var inspectionBudget = Math.Min(currentErrors.Count, maxErrors - errors.Count);
                if (inspectionBudget < currentErrors.Count)
                {
                    errorBudgetExceeded = true;
                }

                if (inspectionBudget > 0)
                {
                    var visual = BuildValidationVisualInfo(current);
                    ElementRef? elementRef = null;

                    for (var errorIndex = 0; errorIndex < inspectionBudget; errorIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            elementRef ??= BuildElementRefWpf(
                                ownerId,
                                current,
                                currentXPath,
                                FindReturnFields.Standard,
                                includeElementId: false);
                            var error = currentErrors[errorIndex];
                            errors.Add(new WpfValidationErrorInfo(
                                Element: elementRef,
                                ErrorIndex: errorIndex,
                                Source: BuildValidationSourceInfo(error),
                                Binding: BuildValidationBindingInfo(error, maxValueLength),
                                Content: BuildValidationErrorContentInfo(error, maxValueLength),
                                Exception: BuildValidationExceptionInfo(error, maxValueLength),
                                Visual: visual));
                        }
                        catch (Exception ex)
                        {
                            scanComplete = false;
                            AddValidationWarning(
                                warnings,
                                warningSet,
                                ref discoveredWarnings,
                                ref warningTextTruncated,
                                $"validation_error_unavailable:{currentXPath}:{errorIndex}:{ex.GetType().Name}");
                        }
                    }
                }
            }

            if (remainingDepth <= 1)
            {
                continue;
            }

            DependencyObject[] rawChildren;
            try
            {
                rawChildren = GetChildrenWpf(
                    current,
                    treeService,
                    request.VisibleOnly,
                    includeOffViewport: true,
                    viewportBounds: null);
            }
            catch (Exception ex)
            {
                scanComplete = false;
                AddValidationWarning(
                    warnings,
                    warningSet,
                    ref discoveredWarnings,
                    ref warningTextTruncated,
                    $"validation_children_unavailable:{currentXPath}:{ex.GetType().Name}");
                continue;
            }

            if (rawChildren.Length == 0)
            {
                continue;
            }

            var labels = rawChildren.Select(GetXPathLabel).ToArray();
            var countsByLabel = labels
                .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var runningIndexByLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var i = rawChildren.Length - 1; i >= 0; i--)
            {
                var child = rawChildren[i];
                var label = labels[i];
                runningIndexByLabel.TryGetValue(label, out var reverseIndex);
                reverseIndex++;
                runningIndexByLabel[label] = reverseIndex;

                var segment = label;
                if (countsByLabel[label] > 1)
                {
                    var forwardIndex = countsByLabel[label] - reverseIndex + 1;
                    segment = $"{label}[{forwardIndex}]";
                }

                stack.Push((child, $"{currentXPath}/{segment}", remainingDepth - 1));
            }
        }

        var rootXPathTruncated = rootXPath.Length > MaxValidationRootXPathLength;
        var warningsTruncated = discoveredWarnings > warnings.Count || warningTextTruncated;
        var truncatedReasons = new List<string>(capacity: 6);
        if (depthBudgetExceeded)
        {
            truncatedReasons.Add("maxDepth");
        }

        if (errorBudgetExceeded)
        {
            truncatedReasons.Add("maxErrors");
        }

        if (nodeBudgetExceeded)
        {
            truncatedReasons.Add("maxNodes");
        }

        if (rootXPathTruncated)
        {
            truncatedReasons.Add("maxRootXPathLength");
        }

        if (discoveredWarnings > warnings.Count)
        {
            truncatedReasons.Add("maxWarnings");
        }

        if (warningTextTruncated)
        {
            truncatedReasons.Add("maxWarningLength");
        }

        var windowHandle = new WindowInteropHelper(window).Handle.ToInt64();
        return new GetValidationErrorsResponse(
            BackendUsed: InspectionBackend.Wpf,
            WindowHandleUsed: windowHandle,
            RootXPath: TruncateProvenanceText(rootXPath, MaxValidationRootXPathLength),
            RootXPathTruncated: rootXPathTruncated,
            DepthUsed: depth,
            Errors: errors,
            ReturnedErrors: errors.Count,
            DiscoveredErrors: discoveredErrors,
            ScannedNodes: scannedNodes,
            ScanComplete: scanComplete,
            Truncated: truncatedReasons.Count > 0,
            TruncatedReasons: truncatedReasons.Count == 0 ? null : truncatedReasons,
            Warnings: warnings.Count == 0 ? null : warnings,
            ReturnedWarnings: warnings.Count,
            DiscoveredWarnings: discoveredWarnings,
            WarningsTruncated: warningsTruncated);
    }

    private static ValidationSourceInfo BuildValidationSourceInfo(ValidationError error)
    {
        var rule = error.RuleInError;
        var ruleType = rule is null
            ? null
            : TruncateProvenanceText(rule.GetType().FullName ?? rule.GetType().Name, 512);

        return rule switch
        {
            DataErrorValidationRule => CreateValidationSource(ValidationSourceKind.DataError, ruleType),
            NotifyDataErrorValidationRule => CreateValidationSource(ValidationSourceKind.NotifyDataError, ruleType),
            ExceptionValidationRule => CreateValidationSource(ValidationSourceKind.Exception, ruleType),
            not null when string.Equals(
                rule.GetType().FullName,
                "System.Windows.Controls.ConversionValidationRule",
                StringComparison.Ordinal) =>
                new ValidationSourceInfo(
                    ValidationSourceKind.Conversion,
                    new ProvenanceEvidence(ProvenanceEvidenceKind.BestEffort, "internal_rule_type_name"),
                    ruleType),
            not null => CreateValidationSource(ValidationSourceKind.ValidationRule, ruleType),
            null when error.Exception is not null =>
                new ValidationSourceInfo(
                    ValidationSourceKind.Exception,
                    new ProvenanceEvidence(ProvenanceEvidenceKind.BestEffort, "exception_without_rule")),
            _ =>
                new ValidationSourceInfo(
                    ValidationSourceKind.Unknown,
                    new ProvenanceEvidence(ProvenanceEvidenceKind.Unavailable, "validation_source_unavailable"))
        };
    }

    private static ValidationSourceInfo CreateValidationSource(
        ValidationSourceKind kind,
        string? ruleType) =>
        new(kind, new ProvenanceEvidence(ProvenanceEvidenceKind.Exact), ruleType);

    private static ValidationBindingInfo BuildValidationBindingInfo(
        ValidationError error,
        int maxValueLength)
    {
        if (error.BindingInError is BindingExpressionBase expression)
        {
            var kind = expression switch
            {
                BindingExpression => ValidationBindingKind.Binding,
                MultiBindingExpression => ValidationBindingKind.MultiBinding,
                PriorityBindingExpression => ValidationBindingKind.PriorityBinding,
                _ => ValidationBindingKind.Unknown
            };

            string? targetProperty = null;
            string? path = null;
            string? status = null;
            try
            {
                targetProperty = expression.TargetProperty?.Name;
                status = expression.Status.ToString();
                if (expression is BindingExpression bindingExpression)
                {
                    path = bindingExpression.ParentBinding.Path?.Path ?? bindingExpression.ParentBinding.XPath;
                }
            }
            catch
            {
            }

            var boundedTargetProperty = TruncateValidationMetadata(
                targetProperty,
                MaxValidationPropertyNameLength,
                out var targetPropertyTruncated);
            var boundedPath = TruncateValidationMetadata(path, maxValueLength, out var pathTruncated);
            var boundedStatus = TruncateValidationMetadata(
                status,
                MaxValidationStatusLength,
                out var statusTruncated);

            return new ValidationBindingInfo(
                kind,
                boundedTargetProperty,
                boundedPath,
                boundedStatus,
                targetPropertyTruncated || pathTruncated || statusTruncated);
        }

        return error.BindingInError is BindingGroup
            ? new ValidationBindingInfo(ValidationBindingKind.BindingGroup)
            : new ValidationBindingInfo(ValidationBindingKind.Unknown);
    }

    private static ValidationErrorContentInfo BuildValidationErrorContentInfo(
        ValidationError error,
        int maxValueLength)
    {
        var content = error.ErrorContent;
        var formatted = FormatSafeProvenanceValueDetails(content, "string", maxValueLength);
        var type = content is null
            ? null
            : TruncateProvenanceText(content.GetType().FullName ?? content.GetType().Name, 512);

        return formatted.RepresentsValue
            ? new ValidationErrorContentInfo(type, formatted.Text, formatted.Truncated)
            : new ValidationErrorContentInfo(
                type,
                Value: null,
                Truncated: false,
                UnavailableReason: BuildFormattingFailureReason(
                    "value_to_string_failed",
                    formatted.FormattingFailureType));
    }

    private static ValidationExceptionInfo? BuildValidationExceptionInfo(
        ValidationError error,
        int maxValueLength)
    {
        var exception = error.Exception;
        if (exception is null)
        {
            return null;
        }

        _ = TryFormatExceptionMessage(
            exception,
            maxValueLength,
            out var message,
            out var messageTruncated,
            out var messageUnavailableReason);

        return new ValidationExceptionInfo(
            Type: TruncateProvenanceText(exception.GetType().FullName ?? exception.GetType().Name, 512),
            Message: message,
            MessageTruncated: messageTruncated,
            MessageUnavailableReason: messageUnavailableReason);
    }

    private static ValidationVisualInfo BuildValidationVisualInfo(DependencyObject element)
    {
        var hasError = false;
        var errorTemplateConfigured = false;
        try
        {
            hasError = Validation.GetHasError(element);
            errorTemplateConfigured = Validation.GetErrorTemplate(element) is not null;
        }
        catch
        {
        }

        DependencyObject adornerSite;
        try
        {
            adornerSite = Validation.GetValidationAdornerSite(element) ?? element;
        }
        catch
        {
            return new ValidationVisualInfo(
                hasError,
                errorTemplateConfigured,
                ValidationAdornerState.Unavailable,
                "adorner_site_unavailable");
        }

        if (adornerSite is not UIElement siteElement)
        {
            return new ValidationVisualInfo(
                hasError,
                errorTemplateConfigured,
                ValidationAdornerState.Unavailable,
                "adorner_site_not_ui_element");
        }

        try
        {
            var layer = AdornerLayer.GetAdornerLayer(siteElement);
            if (layer is null)
            {
                return new ValidationVisualInfo(
                    hasError,
                    errorTemplateConfigured,
                    ValidationAdornerState.Unavailable,
                    "adorner_layer_unavailable");
            }

            var active = layer.GetAdorners(siteElement)?.Any(adorner =>
                string.Equals(
                    adorner.GetType().FullName,
                    "MS.Internal.Controls.TemplatedAdorner",
                    StringComparison.Ordinal)) == true;

            return new ValidationVisualInfo(
                hasError,
                errorTemplateConfigured,
                active ? ValidationAdornerState.Active : ValidationAdornerState.NotObserved,
                active ? null : "validation_adorner_not_observed");
        }
        catch
        {
            return new ValidationVisualInfo(
                hasError,
                errorTemplateConfigured,
                ValidationAdornerState.Unavailable,
                "adorner_inspection_unavailable");
        }
    }

    private static string FormatValidationErrorMessage(ValidationError error, int maxValueLength)
    {
        var content = error.ErrorContent;
        if (content is not null)
        {
            var formatted = FormatSafeProvenanceValueDetails(content, "string", maxValueLength);
            return formatted.Text ?? TruncateProvenanceText(
                content.GetType().FullName ?? content.GetType().Name,
                maxValueLength);
        }

        var exception = error.Exception;
        if (exception is not null)
        {
            if (TryFormatExceptionMessage(
                    exception,
                    maxValueLength,
                    out var message,
                    out _,
                    out _) &&
                message is not null)
            {
                return message;
            }

            var typeName = exception.GetType().FullName ?? exception.GetType().Name;
            return TruncateProvenanceText($"Validation exception ({typeName})", maxValueLength);
        }

        return "Validation error";
    }

    private static bool TryFormatExceptionMessage(
        Exception exception,
        int maxValueLength,
        out string? message,
        out bool truncated,
        out string? unavailableReason)
    {
        message = null;
        truncated = false;
        unavailableReason = null;

        try
        {
            var rawMessage = exception.Message ?? string.Empty;
            message = TruncateProvenanceText(rawMessage, maxValueLength);
            truncated = rawMessage.Length > maxValueLength;
            return true;
        }
        catch (Exception messageFailure)
        {
            unavailableReason = BuildFormattingFailureReason(
                "message_getter_failed",
                TruncateProvenanceText(
                    messageFailure.GetType().FullName ?? messageFailure.GetType().Name,
                    512));
            return false;
        }
    }

    private static void AddValidationWarning(
        List<string> warnings,
        HashSet<string> warningSet,
        ref int discoveredWarnings,
        ref bool warningTextTruncated,
        string warning)
    {
        var textTruncated = warning.Length > MaxValidationWarningLength;
        var boundedWarning = TruncateProvenanceText(warning, MaxValidationWarningLength);
        if (!warningSet.Add(boundedWarning))
        {
            return;
        }

        discoveredWarnings++;
        warningTextTruncated |= textTruncated;
        if (warnings.Count >= MaxValidationWarnings)
        {
            return;
        }

        warnings.Add(boundedWarning);
    }

    private static string? TruncateValidationMetadata(
        string? value,
        int maxLength,
        out bool truncated)
    {
        truncated = value is not null && value.Length > maxLength;
        return value is null ? null : TruncateProvenanceText(value, maxLength);
    }
}
