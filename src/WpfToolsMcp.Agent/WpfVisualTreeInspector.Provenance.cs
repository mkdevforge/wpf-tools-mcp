using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using Snoop.Infrastructure.Helpers;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Agent;

internal static partial class WpfVisualTreeInspector
{
    private const string WinningStyleContributorUnavailable = "style_winner_not_exposed";

    private const string WinningTemplateContributorUnavailable = "template_winner_not_exposed";

    private const string StaticResourceOriginUnavailable = "static_resource_origin_not_retained";

    // Public ResourceDictionary enumeration copies every key and realizes values.
    // Guarded raw storage lets one bucket consume exactly one provenance scan unit.
    private static readonly FieldInfo? ResourceDictionaryBaseDictionaryField =
        typeof(ResourceDictionary).GetField("_baseDictionary", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? ResourceDictionaryMergedDictionariesField =
        typeof(ResourceDictionary).GetField("_mergedDictionaries", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? HashtableBucketsField =
        typeof(Hashtable).GetField("_buckets", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly Type? HashtableBucketType =
        typeof(Hashtable).GetNestedType("Bucket", BindingFlags.NonPublic);

    private static readonly FieldInfo? HashtableBucketKeyField =
        HashtableBucketType?.GetField("key", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? HashtableBucketValueField =
        HashtableBucketType?.GetField("val", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private sealed class ProvenanceScanBudget
    {
        public ProvenanceScanBudget(int limit)
        {
            Limit = Math.Max(0, limit);
            Remaining = Limit;
        }

        public int Limit { get; }

        public int Remaining { get; private set; }

        public int Attempts { get; private set; }

        public bool Exhausted { get; private set; }

        public bool TryConsume()
        {
            if (Remaining <= 0)
            {
                Exhausted = true;
                return false;
            }

            Remaining--;
            Attempts++;
            return true;
        }
    }

    private static DependencyPropertyProvenance BuildDependencyPropertyProvenance(
        DependencyObject element,
        DependencyProperty property,
        string valueFormat,
        int maxStringLength,
        int maxCandidates)
    {
        ValueSource? valueSource = null;
        try
        {
            valueSource = DependencyPropertyHelper.GetValueSource(element, property);
        }
        catch
        {
        }

        var source = valueSource is { } exactSource
            ? new DependencyPropertyValueSourceProvenance(
                MapBaseValueSource(exactSource.BaseValueSource),
                exactSource.IsExpression,
                exactSource.IsAnimated,
                exactSource.IsCoerced,
                exactSource.IsCurrent,
                new ProvenanceEvidence(ProvenanceEvidenceKind.Exact))
            : new DependencyPropertyValueSourceProvenance(
                DependencyPropertyBaseValueSource.Unknown,
                IsExpression: false,
                IsAnimated: false,
                IsCoerced: false,
                IsCurrent: false,
                new ProvenanceEvidence(
                    ProvenanceEvidenceKind.Unavailable,
                    "value_source_unavailable"));

        PropertyMetadata? metadata = null;
        try
        {
            metadata = property.GetMetadata(element.GetType());
        }
        catch
        {
        }

        object? effectiveValue = null;
        var hasEffectiveValue = false;
        try
        {
            effectiveValue = element.GetValue(property);
            hasEffectiveValue = true;
        }
        catch
        {
        }

        BindingExpressionBase? bindingExpression = null;
        try
        {
            bindingExpression = BindingOperations.GetBindingExpressionBase(element, property);
        }
        catch
        {
        }

        BindingProvenance? binding = null;
        if (bindingExpression is not null)
        {
            try
            {
                binding = BuildBindingProvenance(bindingExpression, metadata, maxCandidates, maxStringLength);
            }
            catch
            {
                binding = CreateUnavailableBindingProvenance(bindingExpression);
            }
        }

        StylePropertyProvenance? style = null;
        if (valueSource is { } styleSource && GetStyleProvenanceKind(element, styleSource) is { } styleKind)
        {
            try
            {
                style = BuildStyleProvenance(element, property, styleSource, maxCandidates, maxStringLength)
                    ?? CreateUnavailableStyleProvenance(styleKind);
            }
            catch
            {
                style = CreateUnavailableStyleProvenance(styleKind);
            }
        }

        ResourcePropertyProvenance? resource = null;
        if (bindingExpression is null)
        {
            try
            {
                resource = BuildResourceProvenance(
                    element,
                    property,
                    effectiveValue,
                    hasEffectiveValue,
                    hasBinding: false,
                    valueSource?.IsExpression == true,
                    maxCandidates);
            }
            catch
            {
                resource = CreateUnavailableResourceProvenance(valueSource?.IsExpression == true);
            }
        }

        TemplatePropertyProvenance? template = null;
        if (valueSource is { } templateSource && IsTemplateValueSource(templateSource.BaseValueSource))
        {
            try
            {
                template = BuildTemplateProvenance(element, property, templateSource, maxCandidates, maxStringLength)
                    ?? CreateUnavailableTemplateProvenance(templateSource.BaseValueSource);
            }
            catch
            {
                template = CreateUnavailableTemplateProvenance(templateSource.BaseValueSource);
            }
        }

        InheritancePropertyProvenance? inheritance = null;
        if (valueSource?.BaseValueSource == BaseValueSource.Inherited)
        {
            try
            {
                inheritance = BuildInheritanceProvenance(metadata);
            }
            catch
            {
                inheritance = CreateUnavailableInheritanceProvenance();
            }
        }

        AnimationPropertyProvenance? animation = null;
        if (valueSource?.IsAnimated == true)
        {
            try
            {
                animation = BuildAnimationProvenance(element, property, valueFormat, maxStringLength);
            }
            catch
            {
                animation = CreateUnavailableAnimationProvenance();
            }
        }

        CoercionPropertyProvenance? coercion = null;
        if (valueSource?.IsCoerced == true)
        {
            try
            {
                coercion = BuildCoercionProvenance(metadata);
            }
            catch
            {
                coercion = CreateUnavailableCoercionProvenance();
            }
        }

        DefaultMetadataPropertyProvenance defaultMetadata;
        try
        {
            defaultMetadata = BuildDefaultMetadataProvenance(
                metadata,
                valueSource is null
                    ? null
                    : valueSource.Value.BaseValueSource == BaseValueSource.Default,
                valueFormat,
                maxStringLength);
        }
        catch
        {
            defaultMetadata = CreateUnavailableDefaultMetadataProvenance(
                valueSource is null
                    ? null
                    : valueSource.Value.BaseValueSource == BaseValueSource.Default);
        }

        return new DependencyPropertyProvenance(
            ValueSource: source,
            Binding: binding,
            Style: style,
            Resource: resource,
            Template: template,
            Inheritance: inheritance,
            Animation: animation,
            Coercion: coercion,
            DefaultMetadata: defaultMetadata);
    }

    private static DependencyPropertyProvenance CreateUnavailableDependencyPropertyProvenance() =>
        new(
            ValueSource: new DependencyPropertyValueSourceProvenance(
                DependencyPropertyBaseValueSource.Unknown,
                IsExpression: false,
                IsAnimated: false,
                IsCoerced: false,
                IsCurrent: false,
                new ProvenanceEvidence(
                    ProvenanceEvidenceKind.Unavailable,
                    "provenance_build_failed")),
            Binding: null,
            Style: null,
            Resource: null,
            Template: null,
            Inheritance: null,
            Animation: null,
            Coercion: null,
            DefaultMetadata: CreateUnavailableDefaultMetadataProvenance(isEffectiveValueSource: null));

    private static BindingProvenance CreateUnavailableBindingProvenance(BindingExpressionBase expression) =>
        new(
            Kind: GetBindingKind(expression),
            Path: null,
            SourceKind: null,
            SourceSummary: null,
            DataItemSummary: null,
            ResolvedSourceSummary: null,
            ResolvedSourcePropertyName: null,
            Mode: null,
            EffectiveMode: null,
            UpdateSourceTrigger: null,
            EffectiveUpdateSourceTrigger: null,
            Converter: null,
            Status: null,
            HasError: null,
            HasValidationError: null,
            Children: [],
            ReturnedChildren: 0,
            DiscoveredChildren: 0,
            ScanComplete: false,
            Truncated: false,
            ActiveChildIndex: null,
            ActiveChildOutsideReturnedRange: false,
            Evidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "binding_details_unavailable"));

    private static StylePropertyProvenance CreateUnavailableStyleProvenance(
        (StyleProvenanceKind Kind, ProvenanceEvidence Evidence) kind) =>
        new(
            Kind: kind.Kind,
            KindEvidence: kind.Evidence,
            TargetType: null,
            ResourceKey: null,
            ResourceKeyEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "style_resource_key_not_intrinsic"),
            BasedOnTargetTypes: [],
            Candidates: [],
            ReturnedCandidates: 0,
            DiscoveredCandidates: 0,
            ScannedDeclarations: 0,
            ScanComplete: false,
            Truncated: false,
            TruncatedReason: null,
            ParticipationEvidence: new ProvenanceEvidence(ProvenanceEvidenceKind.Exact),
            StyleDetailsEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "style_details_unavailable"),
            ContributorEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                WinningStyleContributorUnavailable));

    private static ResourcePropertyProvenance CreateUnavailableResourceProvenance(bool isExpression) =>
        new(
            ReferenceKind: isExpression ? "Expression" : "Unknown",
            Key: null,
            Scope: null,
            Candidates: [],
            ReturnedCandidates: 0,
            DiscoveredCandidates: 0,
            ScanAttempts: 0,
            ScannedDictionaries: 0,
            ScannedEntries: 0,
            ScanComplete: false,
            Truncated: false,
            TruncatedReason: null,
            ScanEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "resource_scan_failed"),
            KeyEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "resource_scan_failed"),
            ScopeEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "resource_scan_failed"),
            OriginEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "resource_scan_failed"));

    private static TemplatePropertyProvenance CreateUnavailableTemplateProvenance(BaseValueSource source) =>
        new(
            Kind: source.ToString(),
            TemplateType: null,
            TargetType: null,
            TemplatedParentType: null,
            Candidates: [],
            ReturnedCandidates: 0,
            DiscoveredCandidates: 0,
            ScannedDeclarations: 0,
            ScanComplete: false,
            Truncated: false,
            TruncatedReason: null,
            ParticipationEvidence: new ProvenanceEvidence(ProvenanceEvidenceKind.Exact),
            TemplateDetailsEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "template_details_unavailable"),
            ContributorEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                WinningTemplateContributorUnavailable));

    private static InheritancePropertyProvenance CreateUnavailableInheritanceProvenance() =>
        new(
            MetadataInherits: null,
            ProviderSummary: null,
            ParticipationEvidence: new ProvenanceEvidence(ProvenanceEvidenceKind.Exact),
            ProviderEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "inheritance_provider_not_exposed"));

    private static AnimationPropertyProvenance CreateUnavailableAnimationProvenance() =>
        new(
            BaseValue: null,
            BaseValueType: null,
            BaseValueEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "animation_base_value_unavailable"),
            OriginEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "animation_origin_not_exposed"));

    private static CoercionPropertyProvenance CreateUnavailableCoercionProvenance() =>
        new(
            Callback: null,
            CallbackEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "coercion_callback_unavailable"),
            PreCoercionValueEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "pre_coercion_value_not_exposed"));

    private static DefaultMetadataPropertyProvenance CreateUnavailableDefaultMetadataProvenance(
        bool? isEffectiveValueSource) =>
        new(
            DefaultValue: null,
            DefaultValueType: null,
            DefaultValueEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "metadata_unavailable"),
            MetadataType: null,
            IsEffectiveValueSource: isEffectiveValueSource,
            EffectiveValueSourceEvidence: isEffectiveValueSource.HasValue
                ? new ProvenanceEvidence(ProvenanceEvidenceKind.Exact)
                : new ProvenanceEvidence(
                    ProvenanceEvidenceKind.Unavailable,
                    "value_source_unavailable"),
            Inherits: null,
            BindsTwoWayByDefault: null,
            DefaultUpdateSourceTrigger: null,
            IsAnimationProhibited: null,
            Evidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "metadata_unavailable"));

    private static DependencyPropertyBaseValueSource MapBaseValueSource(BaseValueSource source) => source switch
    {
        BaseValueSource.Default => DependencyPropertyBaseValueSource.Default,
        BaseValueSource.Inherited => DependencyPropertyBaseValueSource.Inherited,
        BaseValueSource.DefaultStyle => DependencyPropertyBaseValueSource.DefaultStyle,
        BaseValueSource.DefaultStyleTrigger => DependencyPropertyBaseValueSource.DefaultStyleTrigger,
        BaseValueSource.Style => DependencyPropertyBaseValueSource.Style,
        BaseValueSource.TemplateTrigger => DependencyPropertyBaseValueSource.TemplateTrigger,
        BaseValueSource.StyleTrigger => DependencyPropertyBaseValueSource.StyleTrigger,
        BaseValueSource.ImplicitStyleReference => DependencyPropertyBaseValueSource.ImplicitStyleReference,
        BaseValueSource.ParentTemplate => DependencyPropertyBaseValueSource.ParentTemplate,
        BaseValueSource.ParentTemplateTrigger => DependencyPropertyBaseValueSource.ParentTemplateTrigger,
        BaseValueSource.Local => DependencyPropertyBaseValueSource.Local,
        _ => DependencyPropertyBaseValueSource.Unknown
    };

    private static BindingProvenance BuildBindingProvenance(
        BindingExpressionBase expression,
        PropertyMetadata? metadata,
        int maxCandidates,
        int maxStringLength)
    {
        string? path = null;
        string? sourceKind = null;
        string? sourceSummary = null;
        string? dataItemSummary = null;
        string? resolvedSourceSummary = null;
        string? resolvedSourcePropertyName = null;
        string? mode = null;
        string? effectiveMode = null;
        string? updateSourceTrigger = null;
        string? effectiveUpdateSourceTrigger = null;
        string? converter = null;
        IReadOnlyList<BindingExpressionBase> childExpressions = [];
        BindingMode parentMode = BindingMode.Default;
        UpdateSourceTrigger parentUpdateSourceTrigger = UpdateSourceTrigger.Default;

        if (expression is BindingExpression bindingExpression)
        {
            PopulateLeafBindingDetails(
                bindingExpression,
                metadata,
                parentMode: null,
                parentUpdateSourceTrigger: null,
                maxStringLength,
                out path,
                out sourceKind,
                out sourceSummary,
                out dataItemSummary,
                out resolvedSourceSummary,
                out resolvedSourcePropertyName,
                out mode,
                out effectiveMode,
                out updateSourceTrigger,
                out effectiveUpdateSourceTrigger,
                out converter);
        }
        else if (expression is MultiBindingExpression multiExpression)
        {
            var binding = multiExpression.ParentMultiBinding;
            parentMode = binding.Mode;
            parentUpdateSourceTrigger = binding.UpdateSourceTrigger;
            sourceKind = "Multiple";
            sourceSummary = $"{multiExpression.BindingExpressions.Count} bindings";
            mode = binding.Mode.ToString();
            effectiveMode = binding.Mode == BindingMode.Default && metadata is null
                ? null
                : ResolveEffectiveMode(binding.Mode, metadata).ToString();
            updateSourceTrigger = binding.UpdateSourceTrigger.ToString();
            effectiveUpdateSourceTrigger =
                binding.UpdateSourceTrigger == UpdateSourceTrigger.Default && metadata is null
                    ? null
                    : ResolveEffectiveUpdateSourceTrigger(
                        binding.UpdateSourceTrigger,
                        metadata).ToString();
            converter = GetTypeName(binding.Converter);
            childExpressions = multiExpression.BindingExpressions;
        }
        else if (expression is PriorityBindingExpression priorityExpression)
        {
            sourceKind = "Priority";
            sourceSummary = $"{priorityExpression.BindingExpressions.Count} bindings";
            childExpressions = priorityExpression.BindingExpressions;
        }

        var returnedChildren = new List<BindingChildProvenance>();
        var childLimit = Math.Min(maxCandidates, childExpressions.Count);
        int? activeChildIndex = null;
        var activeExpression = expression is PriorityBindingExpression priority
            ? priority.ActiveBindingExpression
            : null;

        for (var i = 0; i < childLimit; i++)
        {
            var child = childExpressions[i];
            if (ReferenceEquals(child, activeExpression))
            {
                activeChildIndex = i;
            }

            returnedChildren.Add(BuildBindingChildProvenance(
                child,
                i,
                metadata,
                expression is MultiBindingExpression ? parentMode : null,
                expression is MultiBindingExpression ? parentUpdateSourceTrigger : null,
                maxStringLength));
        }

        var activeChildOutsideReturnedRange =
            activeExpression is not null && activeChildIndex is null && childExpressions.Count > childLimit;

        return new BindingProvenance(
            Kind: GetBindingKind(expression),
            Path: path,
            SourceKind: sourceKind,
            SourceSummary: sourceSummary,
            DataItemSummary: dataItemSummary,
            ResolvedSourceSummary: resolvedSourceSummary,
            ResolvedSourcePropertyName: resolvedSourcePropertyName,
            Mode: mode,
            EffectiveMode: effectiveMode,
            UpdateSourceTrigger: updateSourceTrigger,
            EffectiveUpdateSourceTrigger: effectiveUpdateSourceTrigger,
            Converter: converter,
            Status: expression.Status.ToString(),
            HasError: expression.HasError,
            HasValidationError: expression.HasValidationError,
            Children: returnedChildren,
            ReturnedChildren: returnedChildren.Count,
            DiscoveredChildren: childExpressions.Count,
            ScanComplete: true,
            Truncated: returnedChildren.Count < childExpressions.Count,
            ActiveChildIndex: activeChildIndex,
            ActiveChildOutsideReturnedRange: activeChildOutsideReturnedRange,
            Evidence: new ProvenanceEvidence(ProvenanceEvidenceKind.Exact));
    }

    private static BindingChildProvenance BuildBindingChildProvenance(
        BindingExpressionBase expression,
        int index,
        PropertyMetadata? metadata,
        BindingMode? parentMode,
        UpdateSourceTrigger? parentUpdateSourceTrigger,
        int maxStringLength)
    {
        string? path = null;
        string? sourceKind = null;
        string? sourceSummary = null;
        string? dataItemSummary = null;
        string? resolvedSourceSummary = null;
        string? resolvedSourcePropertyName = null;
        string? mode = null;
        string? effectiveMode = null;
        string? updateSourceTrigger = null;
        string? effectiveUpdateSourceTrigger = null;
        string? converter = null;

        if (expression is BindingExpression bindingExpression)
        {
            PopulateLeafBindingDetails(
                bindingExpression,
                metadata,
                parentMode,
                parentUpdateSourceTrigger,
                maxStringLength,
                out path,
                out sourceKind,
                out sourceSummary,
                out dataItemSummary,
                out resolvedSourceSummary,
                out resolvedSourcePropertyName,
                out mode,
                out effectiveMode,
                out updateSourceTrigger,
                out effectiveUpdateSourceTrigger,
                out converter);
        }

        return new BindingChildProvenance(
            Index: index,
            Kind: GetBindingKind(expression),
            Path: path,
            SourceKind: sourceKind,
            SourceSummary: sourceSummary,
            DataItemSummary: dataItemSummary,
            ResolvedSourceSummary: resolvedSourceSummary,
            ResolvedSourcePropertyName: resolvedSourcePropertyName,
            Mode: mode,
            EffectiveMode: effectiveMode,
            UpdateSourceTrigger: updateSourceTrigger,
            EffectiveUpdateSourceTrigger: effectiveUpdateSourceTrigger,
            Converter: converter,
            Status: expression.Status.ToString(),
            HasError: expression.HasError,
            HasValidationError: expression.HasValidationError);
    }

    private static void PopulateLeafBindingDetails(
        BindingExpression expression,
        PropertyMetadata? metadata,
        BindingMode? parentMode,
        UpdateSourceTrigger? parentUpdateSourceTrigger,
        int maxStringLength,
        out string? path,
        out string? sourceKind,
        out string? sourceSummary,
        out string? dataItemSummary,
        out string? resolvedSourceSummary,
        out string? resolvedSourcePropertyName,
        out string? mode,
        out string? effectiveMode,
        out string? updateSourceTrigger,
        out string? effectiveUpdateSourceTrigger,
        out string? converter)
    {
        var binding = expression.ParentBinding;
        path = TruncateProvenanceText(binding.Path?.Path ?? binding.XPath ?? string.Empty, maxStringLength);
        if (path.Length == 0)
        {
            path = null;
        }

        (sourceKind, sourceSummary) = DescribeConfiguredBindingSource(binding, maxStringLength);
        dataItemSummary = DescribeBindingRuntimeSource(expression.DataItem, maxStringLength);
        resolvedSourceSummary = DescribeBindingRuntimeSource(expression.ResolvedSource, maxStringLength);
        resolvedSourcePropertyName = string.IsNullOrEmpty(expression.ResolvedSourcePropertyName)
            ? null
            : TruncateProvenanceText(expression.ResolvedSourcePropertyName, maxStringLength);
        mode = binding.Mode.ToString();
        var configuredMode = binding.Mode == BindingMode.Default && parentMode is { } inheritedMode
            ? inheritedMode
            : binding.Mode;
        effectiveMode = configuredMode == BindingMode.Default && metadata is null
            ? null
            : ResolveEffectiveMode(configuredMode, metadata).ToString();
        updateSourceTrigger = binding.UpdateSourceTrigger.ToString();
        var configuredUpdateSourceTrigger =
            binding.UpdateSourceTrigger == UpdateSourceTrigger.Default && parentUpdateSourceTrigger is { } inheritedTrigger
                ? inheritedTrigger
                : binding.UpdateSourceTrigger;
        effectiveUpdateSourceTrigger =
            configuredUpdateSourceTrigger == UpdateSourceTrigger.Default && metadata is null
                ? null
                : ResolveEffectiveUpdateSourceTrigger(
                    configuredUpdateSourceTrigger,
                    metadata).ToString();
        converter = GetTypeName(binding.Converter);
    }

    private static (string Kind, string Summary) DescribeConfiguredBindingSource(Binding binding, int maxStringLength)
    {
        if (binding.Source is not null)
        {
            return ("ExplicitSource", DescribeBindingRuntimeSource(binding.Source, maxStringLength) ?? "null");
        }

        if (!string.IsNullOrWhiteSpace(binding.ElementName))
        {
            return ("ElementName", TruncateProvenanceText(binding.ElementName, maxStringLength));
        }

        if (binding.RelativeSource is { } relativeSource)
        {
            var summary = relativeSource.Mode.ToString();
            if (relativeSource.Mode == RelativeSourceMode.FindAncestor)
            {
                summary += $" ancestorType={GetTypeName(relativeSource.AncestorType) ?? "unknown"}";
                summary += $" level={relativeSource.AncestorLevel}";
            }

            return ("RelativeSource", TruncateProvenanceText(summary, maxStringLength));
        }

        return ("DataContext", "Inherited or local DataContext");
    }

    private static string? DescribeBindingRuntimeSource(object? source, int maxStringLength)
    {
        if (source is null)
        {
            return null;
        }

        if (ReferenceEquals(source, BindingOperations.DisconnectedSource))
        {
            return "{DisconnectedSource}";
        }

        var typeName = GetTypeName(source) ?? "unknown";
        if (source is FrameworkElement frameworkElement)
        {
            var name = frameworkElement.Name;
            var automationId = AutomationProperties.GetAutomationId(frameworkElement);
            if (!string.IsNullOrWhiteSpace(automationId))
            {
                return TruncateProvenanceText($"{typeName} automationId={automationId}", maxStringLength);
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                return TruncateProvenanceText($"{typeName} name={name}", maxStringLength);
            }
        }

        return TruncateProvenanceText(typeName, maxStringLength);
    }

    private static BindingMode ResolveEffectiveMode(BindingMode mode, PropertyMetadata? metadata)
    {
        if (mode != BindingMode.Default)
        {
            return mode;
        }

        return metadata is FrameworkPropertyMetadata { BindsTwoWayByDefault: true }
            ? BindingMode.TwoWay
            : BindingMode.OneWay;
    }

    private static UpdateSourceTrigger ResolveEffectiveUpdateSourceTrigger(
        UpdateSourceTrigger trigger,
        PropertyMetadata? metadata)
    {
        if (trigger != UpdateSourceTrigger.Default)
        {
            return trigger;
        }

        return metadata is FrameworkPropertyMetadata frameworkMetadata
            ? frameworkMetadata.DefaultUpdateSourceTrigger
            : UpdateSourceTrigger.PropertyChanged;
    }

    private static string GetBindingKind(BindingExpressionBase expression) => expression switch
    {
        BindingExpression => "Binding",
        MultiBindingExpression => "MultiBinding",
        PriorityBindingExpression => "PriorityBinding",
        _ => GetTypeName(expression) ?? "BindingExpression"
    };

    private static (StyleProvenanceKind Kind, ProvenanceEvidence Evidence)? GetStyleProvenanceKind(
        DependencyObject element,
        ValueSource source)
    {
        if (source.BaseValueSource is BaseValueSource.DefaultStyle or BaseValueSource.DefaultStyleTrigger)
        {
            return (
                StyleProvenanceKind.Theme,
                new ProvenanceEvidence(ProvenanceEvidenceKind.Exact));
        }

        if (source.BaseValueSource == BaseValueSource.ImplicitStyleReference)
        {
            return (
                StyleProvenanceKind.Implicit,
                new ProvenanceEvidence(ProvenanceEvidenceKind.Exact));
        }

        if (source.BaseValueSource is not (BaseValueSource.Style or BaseValueSource.StyleTrigger))
        {
            return null;
        }

        return IsImplicitStyle(element) switch
        {
            true => (
                StyleProvenanceKind.Implicit,
                new ProvenanceEvidence(ProvenanceEvidenceKind.Exact)),
            false => (
                StyleProvenanceKind.Explicit,
                new ProvenanceEvidence(ProvenanceEvidenceKind.Exact)),
            null => (
                StyleProvenanceKind.Unknown,
                new ProvenanceEvidence(
                    ProvenanceEvidenceKind.Unavailable,
                    "style_kind_unavailable"))
        };
    }

    private static StylePropertyProvenance? BuildStyleProvenance(
        DependencyObject element,
        DependencyProperty property,
        ValueSource source,
        int maxCandidates,
        int maxStringLength)
    {
        var styleKind = GetStyleProvenanceKind(element, source);

        if (styleKind is null)
        {
            return null;
        }

        var style = TryGetRelevantStyle(element, styleKind.Value.Kind);
        var basedOn = new List<string>();
        var candidates = new List<PropertyContributorCandidate>();
        var workRemaining = maxCandidates;
        var scannedDeclarations = 0;
        var discoveredCandidates = 0;
        var scanComplete = true;
        var styles = new List<Style>();

        if (style is not null)
        {
            styles.Add(style);
            var current = style;
            while (true)
            {
                if (workRemaining <= 0)
                {
                    scanComplete = false;
                    break;
                }

                workRemaining--;
                var next = current.BasedOn;
                if (next is null)
                {
                    break;
                }

                basedOn.Add(GetTypeName(next.TargetType) ?? "unknown");
                styles.Add(next);
                current = next;
            }
        }

        var inspectTriggers = source.BaseValueSource is BaseValueSource.StyleTrigger or BaseValueSource.DefaultStyleTrigger;
        var inspectSetters = source.BaseValueSource is BaseValueSource.Style or BaseValueSource.DefaultStyle;

        foreach (var candidateStyle in styles)
        {
            if (inspectSetters)
            {
                if (workRemaining <= 0)
                {
                    scanComplete = false;
                    break;
                }

                ScanSetterCollection(
                    candidateStyle.Setters,
                    property,
                    "StyleSetter",
                    GetTypeName(candidateStyle.TargetType),
                    conditions: null,
                    maxStringLength,
                    ref workRemaining,
                    ref scannedDeclarations,
                    ref discoveredCandidates,
                    ref scanComplete,
                    candidates);
            }

            if (inspectTriggers)
            {
                if (workRemaining <= 0)
                {
                    scanComplete = false;
                    break;
                }

                ScanTriggerCollection(
                    candidateStyle.Triggers,
                    property,
                    "StyleTrigger",
                    GetTypeName(candidateStyle.TargetType),
                    maxStringLength,
                    ref workRemaining,
                    ref scannedDeclarations,
                    ref discoveredCandidates,
                    ref scanComplete,
                    candidates);
            }

            if (!scanComplete && workRemaining <= 0)
            {
                break;
            }
        }

        if (style is null)
        {
            scanComplete = false;
        }

        var contributorEvidence = candidates.Count > 0
            ? new ProvenanceEvidence(ProvenanceEvidenceKind.BestEffort, WinningStyleContributorUnavailable)
            : new ProvenanceEvidence(ProvenanceEvidenceKind.Unavailable, WinningStyleContributorUnavailable);
        var styleDetailsEvidence = style is null
            ? new ProvenanceEvidence(ProvenanceEvidenceKind.Unavailable, "style_details_unavailable")
            : styleKind.Value.Kind == StyleProvenanceKind.Theme
                ? new ProvenanceEvidence(ProvenanceEvidenceKind.BestEffort, "theme_style_internal_access")
                : new ProvenanceEvidence(ProvenanceEvidenceKind.Exact);

        var scanTruncated = !scanComplete && workRemaining <= 0;
        return new StylePropertyProvenance(
            Kind: styleKind.Value.Kind,
            KindEvidence: styleKind.Value.Evidence,
            TargetType: GetTypeName(style?.TargetType),
            ResourceKey: null,
            ResourceKeyEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "style_resource_key_not_intrinsic"),
            BasedOnTargetTypes: basedOn,
            Candidates: candidates,
            ReturnedCandidates: candidates.Count,
            DiscoveredCandidates: discoveredCandidates,
            ScannedDeclarations: scannedDeclarations,
            ScanComplete: scanComplete,
            Truncated: scanTruncated,
            TruncatedReason: scanTruncated ? "maxProvenanceCandidates" : null,
            ParticipationEvidence: new ProvenanceEvidence(ProvenanceEvidenceKind.Exact),
            StyleDetailsEvidence: styleDetailsEvidence,
            ContributorEvidence: contributorEvidence);
    }

    private static bool? IsImplicitStyle(DependencyObject element)
    {
        try
        {
            return element switch
            {
                FrameworkElement frameworkElement =>
                    DependencyPropertyHelper.GetValueSource(frameworkElement, FrameworkElement.StyleProperty)
                        .BaseValueSource == BaseValueSource.ImplicitStyleReference,
                FrameworkContentElement contentElement =>
                    DependencyPropertyHelper.GetValueSource(contentElement, FrameworkContentElement.StyleProperty)
                        .BaseValueSource == BaseValueSource.ImplicitStyleReference,
                _ => false
            };
        }
        catch
        {
            return null;
        }
    }

    private static Style? TryGetRelevantStyle(DependencyObject element, StyleProvenanceKind kind)
    {
        try
        {
            return (element, kind) switch
            {
                (FrameworkElement frameworkElement, StyleProvenanceKind.Theme) =>
                    FrameworkElementHelper.GetThemeStyle(frameworkElement),
                (FrameworkContentElement contentElement, StyleProvenanceKind.Theme) =>
                    FrameworkElementHelper.GetThemeStyle(contentElement),
                (FrameworkElement frameworkElement, _) => frameworkElement.Style,
                (FrameworkContentElement contentElement, _) => contentElement.Style,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static TemplatePropertyProvenance? BuildTemplateProvenance(
        DependencyObject element,
        DependencyProperty property,
        ValueSource source,
        int maxCandidates,
        int maxStringLength)
    {
        if (source.BaseValueSource is not (
            BaseValueSource.TemplateTrigger or
            BaseValueSource.ParentTemplate or
            BaseValueSource.ParentTemplateTrigger))
        {
            return null;
        }

        var frameworkElement = element as FrameworkElement;
        var templatedParent = frameworkElement?.TemplatedParent as FrameworkElement;
        var templateHost = source.BaseValueSource is BaseValueSource.ParentTemplate or BaseValueSource.ParentTemplateTrigger
            ? templatedParent
            : frameworkElement;
        var templateWasPublic = templateHost is Control;
        var template = TryGetAppliedTemplate(templateHost);
        var candidates = new List<PropertyContributorCandidate>();
        var workRemaining = maxCandidates;
        var scannedDeclarations = 0;
        var discoveredCandidates = 0;
        var scanComplete = true;

        if (source.BaseValueSource is BaseValueSource.TemplateTrigger or BaseValueSource.ParentTemplateTrigger)
        {
            if (workRemaining <= 0)
            {
                scanComplete = false;
            }
            else if (TryGetTemplateTriggers(template) is { } templateTriggers)
            {
                ScanTriggerCollection(
                    templateTriggers,
                    property,
                    "TemplateTrigger",
                    GetTemplateTargetType(template),
                    maxStringLength,
                    ref workRemaining,
                    ref scannedDeclarations,
                    ref discoveredCandidates,
                    ref scanComplete,
                    candidates);
            }
            else
            {
                scanComplete = false;
            }
        }

        var contributorEvidence = candidates.Count > 0
            ? new ProvenanceEvidence(ProvenanceEvidenceKind.BestEffort, WinningTemplateContributorUnavailable)
            : new ProvenanceEvidence(ProvenanceEvidenceKind.Unavailable, WinningTemplateContributorUnavailable);
        var detailsEvidence = template is null
            ? new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "template_not_available")
            : templateWasPublic
                ? new ProvenanceEvidence(ProvenanceEvidenceKind.Exact)
                : new ProvenanceEvidence(
                    ProvenanceEvidenceKind.BestEffort,
                    "template_internal_access");

        return new TemplatePropertyProvenance(
            Kind: source.BaseValueSource.ToString(),
            TemplateType: GetTypeName(template),
            TargetType: GetTemplateTargetType(template),
            TemplatedParentType: GetTypeName(templatedParent),
            Candidates: candidates,
            ReturnedCandidates: candidates.Count,
            DiscoveredCandidates: discoveredCandidates,
            ScannedDeclarations: scannedDeclarations,
            ScanComplete: scanComplete,
            Truncated: !scanComplete && workRemaining <= 0,
            TruncatedReason: !scanComplete && workRemaining <= 0 ? "maxProvenanceCandidates" : null,
            ParticipationEvidence: new ProvenanceEvidence(ProvenanceEvidenceKind.Exact),
            TemplateDetailsEvidence: detailsEvidence,
            ContributorEvidence: contributorEvidence);
    }

    private static bool IsTemplateValueSource(BaseValueSource source) => source is
        BaseValueSource.TemplateTrigger or
        BaseValueSource.ParentTemplate or
        BaseValueSource.ParentTemplateTrigger;

    private static FrameworkTemplate? TryGetAppliedTemplate(FrameworkElement? element)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            return element is Control control
                ? control.Template
                : FrameworkElementHelper.GetTemplate(element);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetTemplateTargetType(FrameworkTemplate? template) => template switch
    {
        ControlTemplate controlTemplate => GetTypeName(controlTemplate.TargetType),
        DataTemplate dataTemplate => FormatSafeProvenanceValue(dataTemplate.DataType, "string", 200),
        _ => null
    };

    private static TriggerCollection? TryGetTemplateTriggers(FrameworkTemplate? template) => template switch
    {
        ControlTemplate controlTemplate => controlTemplate.Triggers,
        DataTemplate dataTemplate => dataTemplate.Triggers,
        _ => null
    };

    private static void ScanSetterCollection(
        SetterBaseCollection setters,
        DependencyProperty property,
        string candidateKind,
        string? declaringType,
        string? conditions,
        int maxStringLength,
        ref int workRemaining,
        ref int scannedDeclarations,
        ref int discoveredCandidates,
        ref bool scanComplete,
        List<PropertyContributorCandidate> candidates)
    {
        for (var i = 0; i < setters.Count; i++)
        {
            if (workRemaining <= 0)
            {
                scanComplete = false;
                return;
            }

            workRemaining--;
            scannedDeclarations++;
            var setterBase = setters[i];
            if (setterBase is not Setter setter || setter.Property != property)
            {
                continue;
            }

            discoveredCandidates++;
            candidates.Add(new PropertyContributorCandidate(
                Kind: candidateKind,
                DeclaringType: declaringType,
                TargetName: string.IsNullOrWhiteSpace(setter.TargetName)
                    ? null
                    : TruncateProvenanceText(setter.TargetName, maxStringLength),
                Value: FormatSafeProvenanceValue(setter.Value, "string", maxStringLength),
                Conditions: conditions,
                Evidence: new ProvenanceEvidence(
                    ProvenanceEvidenceKind.BestEffort,
                    candidateKind == "TemplateTrigger"
                        ? WinningTemplateContributorUnavailable
                        : WinningStyleContributorUnavailable)));
        }
    }

    private static void ScanTriggerCollection(
        TriggerCollection triggers,
        DependencyProperty property,
        string candidateKind,
        string? declaringType,
        int maxStringLength,
        ref int workRemaining,
        ref int scannedDeclarations,
        ref int discoveredCandidates,
        ref bool scanComplete,
        List<PropertyContributorCandidate> candidates)
    {
        for (var i = 0; i < triggers.Count; i++)
        {
            if (workRemaining <= 0)
            {
                scanComplete = false;
                return;
            }

            workRemaining--;
            scannedDeclarations++;
            var trigger = triggers[i];
            var setters = GetTriggerSetters(trigger);
            if (setters is null)
            {
                continue;
            }

            ScanSetterCollection(
                setters,
                property,
                candidateKind,
                declaringType,
                DescribeTriggerConditions(trigger, maxStringLength),
                maxStringLength,
                ref workRemaining,
                ref scannedDeclarations,
                ref discoveredCandidates,
                ref scanComplete,
                candidates);

            if (!scanComplete && workRemaining <= 0)
            {
                return;
            }
        }
    }

    private static SetterBaseCollection? GetTriggerSetters(TriggerBase trigger) => trigger switch
    {
        Trigger propertyTrigger => propertyTrigger.Setters,
        DataTrigger dataTrigger => dataTrigger.Setters,
        MultiTrigger multiTrigger => multiTrigger.Setters,
        MultiDataTrigger multiDataTrigger => multiDataTrigger.Setters,
        _ => null
    };

    private static string DescribeTriggerConditions(TriggerBase trigger, int maxStringLength)
    {
        var description = trigger switch
        {
            Trigger propertyTrigger =>
                $"{GetTypeName(propertyTrigger.Property?.OwnerType) ?? "unknown"}.{propertyTrigger.Property?.Name} == " +
                FormatSafeProvenanceValue(propertyTrigger.Value, "string", maxStringLength),
            DataTrigger dataTrigger =>
                $"Binding({DescribeBindingPath(dataTrigger.Binding)}) == " +
                FormatSafeProvenanceValue(dataTrigger.Value, "string", maxStringLength),
            MultiTrigger multiTrigger => $"{multiTrigger.Conditions.Count} property conditions",
            MultiDataTrigger multiDataTrigger => $"{multiDataTrigger.Conditions.Count} binding conditions",
            _ => GetTypeName(trigger) ?? "unknown"
        };

        return TruncateProvenanceText(description, maxStringLength);
    }

    private static string DescribeBindingPath(BindingBase? binding) => binding switch
    {
        Binding leaf => leaf.Path?.Path ?? leaf.XPath ?? "(self)",
        MultiBinding => "MultiBinding",
        PriorityBinding => "PriorityBinding",
        null => "unknown",
        _ => GetTypeName(binding) ?? "BindingBase"
    };

    private static ResourcePropertyProvenance? BuildResourceProvenance(
        DependencyObject element,
        DependencyProperty property,
        object? effectiveValue,
        bool hasEffectiveValue,
        bool hasBinding,
        bool isExpression,
        int maxCandidates)
    {
        if (hasBinding)
        {
            return null;
        }

        object? dynamicResourceKey = null;
        var dynamicResourceDetected = TryGetDynamicResourceKey(element, property, out dynamicResourceKey);
        var candidates = new List<ResourceCandidateProvenance>();
        var visited = new HashSet<ResourceDictionary>(ReferenceEqualityComparer.Instance);
        var budget = new ProvenanceScanBudget(maxCandidates);
        var scannedDictionaries = 0;
        var scannedEntries = 0;
        var discoveredCandidates = 0;
        string? scanIncompleteReason = null;

        if (dynamicResourceDetected || hasEffectiveValue)
        {
            var parent = element;
            var parentIndex = 0;
            while (parent is not null)
            {
                if (!budget.TryConsume())
                {
                    break;
                }

                var scopePrefix = parentIndex == 0
                    ? "Element"
                    : TruncateProvenanceText(
                        $"Ancestor[{parentIndex}:{GetTypeName(parent) ?? "unknown"}]",
                        512);
                foreach (var (dictionary, suffix) in GetResourceDictionaries(parent))
                {
                    ProbeResourceDictionary(
                        dictionary,
                        scopePrefix + suffix,
                        dynamicResourceDetected ? dynamicResourceKey : null,
                        dynamicResourceDetected,
                        effectiveValue,
                        hasEffectiveValue,
                        budget,
                        visited,
                        candidates,
                        ref scannedDictionaries,
                        ref scannedEntries,
                        ref discoveredCandidates,
                        ref scanIncompleteReason);

                    if (budget.Exhausted)
                    {
                        break;
                    }
                }

                if (budget.Exhausted)
                {
                    break;
                }

                parent = GetResourceLookupParent(parent);
                parentIndex++;
            }

            if (!budget.Exhausted && Application.Current is { } application && budget.TryConsume())
            {
                ProbeResourceDictionary(
                    application.Resources,
                    "Application.Resources",
                    dynamicResourceDetected ? dynamicResourceKey : null,
                    dynamicResourceDetected,
                    effectiveValue,
                    hasEffectiveValue,
                    budget,
                    visited,
                    candidates,
                    ref scannedDictionaries,
                    ref scannedEntries,
                    ref discoveredCandidates,
                    ref scanIncompleteReason);
            }
        }

        var scanComplete = !budget.Exhausted && scanIncompleteReason is null;

        var referenceKind = dynamicResourceDetected
            ? "DynamicResource"
            : candidates.Count > 0
                ? "ResourceCandidate"
                : "Unknown";
        var key = dynamicResourceDetected
            ? FormatSafeResourceKey(dynamicResourceKey)
            : scanComplete && candidates.Count == 1
                ? candidates[0].Key
                : null;
        var scope = scanComplete && candidates.Count == 1 ? candidates[0].Scope : null;
        var keyEvidence = key is not null
            ? new ProvenanceEvidence(
                ProvenanceEvidenceKind.BestEffort,
                dynamicResourceDetected
                    ? "dynamic_resource_internal_expression"
                    : StaticResourceOriginUnavailable)
            : new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                dynamicResourceDetected
                    ? "dynamic_resource_key_not_safely_serializable"
                    : StaticResourceOriginUnavailable);
        var scopeEvidence = scope is not null
            ? new ProvenanceEvidence(
                ProvenanceEvidenceKind.BestEffort,
                "resource_scope_candidate")
            : new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "resource_scope_unavailable");
        var originEvidence = dynamicResourceDetected
            ? new ProvenanceEvidence(
                ProvenanceEvidenceKind.BestEffort,
                "dynamic_resource_internal_expression")
            : candidates.Count > 0
                ? new ProvenanceEvidence(
                    ProvenanceEvidenceKind.BestEffort,
                    scanComplete ? StaticResourceOriginUnavailable : "resource_scan_incomplete")
            : new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                scanIncompleteReason is not null
                    ? scanIncompleteReason
                    : isExpression
                    ? "non_binding_expression_origin_unavailable"
                    : StaticResourceOriginUnavailable);

        return new ResourcePropertyProvenance(
            ReferenceKind: referenceKind,
            Key: key,
            Scope: scope,
            Candidates: candidates,
            ReturnedCandidates: candidates.Count,
            DiscoveredCandidates: discoveredCandidates,
            ScanAttempts: budget.Attempts,
            ScannedDictionaries: scannedDictionaries,
            ScannedEntries: scannedEntries,
            ScanComplete: scanComplete,
            Truncated: budget.Exhausted,
            TruncatedReason: budget.Exhausted ? "maxProvenanceCandidates" : null,
            ScanEvidence: scanComplete
                ? new ProvenanceEvidence(
                    ProvenanceEvidenceKind.BestEffort,
                    "resource_dictionary_internal_access")
                : new ProvenanceEvidence(
                    ProvenanceEvidenceKind.Unavailable,
                    budget.Exhausted
                        ? "resource_scan_budget_exhausted"
                        : scanIncompleteReason ?? "resource_scan_incomplete"),
            KeyEvidence: keyEvidence,
            ScopeEvidence: scopeEvidence,
            OriginEvidence: originEvidence);
    }

    private static bool TryGetDynamicResourceKey(
        DependencyObject element,
        DependencyProperty property,
        out object? resourceKey)
    {
        resourceKey = null;
        object? localValue;
        try
        {
            localValue = element.ReadLocalValue(property);
        }
        catch
        {
            return false;
        }

        var localType = localValue?.GetType();
        if (!string.Equals(
                localType?.FullName,
                "System.Windows.ResourceReferenceExpression",
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            resourceKey = localType!.GetProperty(
                    "ResourceKey",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(localValue);
            return resourceKey is not null;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<(ResourceDictionary Dictionary, string ScopeSuffix)> GetResourceDictionaries(
        DependencyObject element)
    {
        if (element is FrameworkElement frameworkElement)
        {
            if (frameworkElement.Resources.Count > 0)
            {
                yield return (frameworkElement.Resources, ".Resources");
            }

            if (frameworkElement.Style?.Resources is { Count: > 0 } styleResources)
            {
                yield return (styleResources, ".Style.Resources");
            }

            if (TryGetAppliedTemplate(frameworkElement)?.Resources is { Count: > 0 } templateResources)
            {
                yield return (templateResources, ".Template.Resources");
            }

            if (TryGetRelevantStyle(frameworkElement, StyleProvenanceKind.Theme)?.Resources is { Count: > 0 }
                themeStyleResources)
            {
                yield return (themeStyleResources, ".ThemeStyle.Resources");
            }
        }
        else if (element is FrameworkContentElement contentElement)
        {
            if (contentElement.Resources.Count > 0)
            {
                yield return (contentElement.Resources, ".Resources");
            }

            if (contentElement.Style?.Resources is { Count: > 0 } styleResources)
            {
                yield return (styleResources, ".Style.Resources");
            }

            if (TryGetRelevantStyle(contentElement, StyleProvenanceKind.Theme)?.Resources is { Count: > 0 }
                themeStyleResources)
            {
                yield return (themeStyleResources, ".ThemeStyle.Resources");
            }
        }
    }

    private static void ProbeResourceDictionary(
        ResourceDictionary dictionary,
        string scope,
        object? knownKey,
        bool hasKnownKey,
        object? effectiveValue,
        bool hasEffectiveValue,
        ProvenanceScanBudget budget,
        HashSet<ResourceDictionary> visited,
        List<ResourceCandidateProvenance> candidates,
        ref int scannedDictionaries,
        ref int scannedEntries,
        ref int discoveredCandidates,
        ref string? scanIncompleteReason)
    {
        if (!budget.TryConsume())
        {
            return;
        }

        if (!visited.Add(dictionary))
        {
            return;
        }

        scannedDictionaries++;
        if (!TryGetResourceDictionaryStorage(dictionary, out var buckets))
        {
            scanIncompleteReason ??= "resource_dictionary_scan_unavailable";
            return;
        }

        var canMatchKnownKey = hasKnownKey && knownKey is not null && IsSafeResourceKey(knownKey);
        for (var i = 0; i < buckets.Length; i++)
        {
            if (!budget.TryConsume())
            {
                return;
            }

            var bucket = buckets.GetValue(i);
            if (bucket is null)
            {
                continue;
            }

            var key = HashtableBucketKeyField!.GetValue(bucket);
            if (key is null || ReferenceEquals(key, buckets))
            {
                continue;
            }

            scannedEntries++;
            var keyIsSafe = IsSafeResourceKey(key);
            if (canMatchKnownKey)
            {
                if (keyIsSafe && AreSafeResourceKeysEqual(key, knownKey!))
                {
                    AddResourceCandidate(key, scope, candidates, ref discoveredCandidates, budget.Limit);
                }

                continue;
            }

            var storedValue = HashtableBucketValueField!.GetValue(bucket);
            if (IsDeferredResourceValue(storedValue))
            {
                scanIncompleteReason ??= "resource_deferred_value_not_realized";
                continue;
            }

            if (hasEffectiveValue && AreSafeResourceCandidateValuesEqual(storedValue, effectiveValue))
            {
                AddResourceCandidate(
                    keyIsSafe ? key : null,
                    scope,
                    candidates,
                    ref discoveredCandidates,
                    budget.Limit);
            }
        }

        if (!TryGetMergedResourceDictionaries(dictionary, out var mergedDictionaries))
        {
            scanIncompleteReason ??= "resource_dictionary_scan_unavailable";
            return;
        }

        if (mergedDictionaries is null)
        {
            return;
        }

        for (var i = mergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (!budget.TryConsume())
            {
                return;
            }

            if (mergedDictionaries[i] is not ResourceDictionary mergedDictionary)
            {
                continue;
            }

            ProbeResourceDictionary(
                mergedDictionary,
                TruncateProvenanceText($"{scope}.MergedDictionaries[{i}]", 512),
                knownKey,
                hasKnownKey,
                effectiveValue,
                hasEffectiveValue,
                budget,
                visited,
                candidates,
                ref scannedDictionaries,
                ref scannedEntries,
                ref discoveredCandidates,
                ref scanIncompleteReason);

            if (budget.Exhausted)
            {
                return;
            }
        }
    }

    private static bool IsDeferredResourceValue(object? value) =>
        string.Equals(
            value?.GetType().FullName,
            "System.Windows.Baml2006.KeyRecord",
            StringComparison.Ordinal);

    private static bool TryGetResourceDictionaryStorage(
        ResourceDictionary dictionary,
        out Array buckets)
    {
        buckets = null!;
        if (ResourceDictionaryBaseDictionaryField is null ||
            HashtableBucketsField is null ||
            HashtableBucketKeyField is null ||
            HashtableBucketValueField is null)
        {
            return false;
        }

        try
        {
            if (ResourceDictionaryBaseDictionaryField.GetValue(dictionary) is not Hashtable baseDictionary ||
                HashtableBucketsField.GetValue(baseDictionary) is not Array storage)
            {
                return false;
            }

            buckets = storage;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetMergedResourceDictionaries(
        ResourceDictionary dictionary,
        out IList? mergedDictionaries)
    {
        mergedDictionaries = null;
        if (ResourceDictionaryMergedDictionariesField is null)
        {
            return false;
        }

        try
        {
            mergedDictionaries = ResourceDictionaryMergedDictionariesField.GetValue(dictionary) as IList;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AddResourceCandidate(
        object? key,
        string scope,
        List<ResourceCandidateProvenance> candidates,
        ref int discoveredCandidates,
        int maxCandidates)
    {
        discoveredCandidates++;
        if (candidates.Count >= maxCandidates)
        {
            return;
        }

        candidates.Add(new ResourceCandidateProvenance(
            Key: FormatSafeResourceKey(key),
            Scope: TruncateProvenanceText(scope, 512),
            Evidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.BestEffort,
                "resource_reference_candidate")));
    }

    private static DependencyObject? GetResourceLookupParent(DependencyObject element)
    {
        try
        {
            var logicalParent = LogicalTreeHelper.GetParent(element);
            if (logicalParent is not null)
            {
                return logicalParent;
            }
        }
        catch
        {
        }

        try
        {
            return element is Visual or Visual3D
                ? VisualTreeHelper.GetParent(element)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSafeResourceKey(object key)
    {
        var type = key.GetType();
        return key is string or Type or ComponentResourceKey ||
               type.IsEnum ||
               type == typeof(char) ||
               type == typeof(bool) ||
               type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong) ||
               type == typeof(Guid);
    }

    private static bool AreSafeResourceCandidateValuesEqual(object? candidate, object? effectiveValue)
    {
        if (ReferenceEquals(candidate, effectiveValue))
        {
            return true;
        }

        if (candidate is null || effectiveValue is null || candidate.GetType() != effectiveValue.GetType())
        {
            return false;
        }

        var type = candidate.GetType();
        if (!IsSafeScalarType(type))
        {
            return false;
        }

        return candidate.Equals(effectiveValue);
    }

    private static bool AreSafeResourceKeysEqual(object candidate, object expected)
    {
        if (ReferenceEquals(candidate, expected))
        {
            return true;
        }

        var type = candidate.GetType();
        if (type != expected.GetType() || candidate is Type or ComponentResourceKey)
        {
            return false;
        }

        return IsSafeScalarType(type) && candidate.Equals(expected);
    }

    private static bool IsSafeScalarType(Type type) =>
        type.IsEnum ||
        type == typeof(string) ||
        type == typeof(char) ||
        type == typeof(bool) ||
        type == typeof(byte) ||
        type == typeof(sbyte) ||
        type == typeof(short) ||
        type == typeof(ushort) ||
        type == typeof(int) ||
        type == typeof(uint) ||
        type == typeof(long) ||
        type == typeof(ulong) ||
        type == typeof(float) ||
        type == typeof(double) ||
        type == typeof(decimal) ||
        type == typeof(Guid) ||
        type == typeof(DateTime) ||
        type == typeof(DateTimeOffset) ||
        type == typeof(TimeSpan);

    private static InheritancePropertyProvenance BuildInheritanceProvenance(PropertyMetadata? metadata) =>
        new(
            MetadataInherits: metadata is FrameworkPropertyMetadata frameworkMetadata
                ? frameworkMetadata.Inherits
                : metadata is null
                    ? null
                    : false,
            ProviderSummary: null,
            ParticipationEvidence: new ProvenanceEvidence(ProvenanceEvidenceKind.Exact),
            ProviderEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "inheritance_provider_not_exposed"));

    private static AnimationPropertyProvenance BuildAnimationProvenance(
        DependencyObject element,
        DependencyProperty property,
        string valueFormat,
        int maxStringLength)
    {
        if (element is IAnimatable animatable)
        {
            try
            {
                var baseValue = animatable.GetAnimationBaseValue(property);
                var canFormatBaseValue = CanFormatProvenanceValueExactly(baseValue, valueFormat);
                return new AnimationPropertyProvenance(
                    BaseValue: canFormatBaseValue
                        ? FormatSafeProvenanceValue(baseValue, valueFormat, maxStringLength)
                        : null,
                    BaseValueType: GetTypeName(baseValue),
                    BaseValueEvidence: canFormatBaseValue
                        ? new ProvenanceEvidence(ProvenanceEvidenceKind.Exact)
                        : new ProvenanceEvidence(
                            ProvenanceEvidenceKind.Unavailable,
                            "value_not_safely_serializable"),
                    OriginEvidence: new ProvenanceEvidence(
                        ProvenanceEvidenceKind.Unavailable,
                        "animation_origin_not_exposed"));
            }
            catch
            {
            }
        }

        return new AnimationPropertyProvenance(
            BaseValue: null,
            BaseValueType: null,
            BaseValueEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "animation_base_value_unavailable"),
            OriginEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "animation_origin_not_exposed"));
    }

    private static CoercionPropertyProvenance BuildCoercionProvenance(PropertyMetadata? metadata)
    {
        var callback = metadata?.CoerceValueCallback;
        return new CoercionPropertyProvenance(
            Callback: callback is null ? null : DescribeDelegate(callback),
            CallbackEvidence: callback is null
                ? new ProvenanceEvidence(
                    ProvenanceEvidenceKind.Unavailable,
                    "coercion_callback_unavailable")
                : new ProvenanceEvidence(ProvenanceEvidenceKind.Exact),
            PreCoercionValueEvidence: new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                "pre_coercion_value_not_exposed"));
    }

    private static DefaultMetadataPropertyProvenance BuildDefaultMetadataProvenance(
        PropertyMetadata? metadata,
        bool? isEffectiveValueSource,
        string valueFormat,
        int maxStringLength)
    {
        if (metadata is null)
        {
            return new DefaultMetadataPropertyProvenance(
                DefaultValue: null,
                DefaultValueType: null,
                DefaultValueEvidence: new ProvenanceEvidence(
                    ProvenanceEvidenceKind.Unavailable,
                    "metadata_unavailable"),
                MetadataType: null,
                IsEffectiveValueSource: isEffectiveValueSource,
                EffectiveValueSourceEvidence: isEffectiveValueSource.HasValue
                    ? new ProvenanceEvidence(ProvenanceEvidenceKind.Exact)
                    : new ProvenanceEvidence(
                        ProvenanceEvidenceKind.Unavailable,
                        "value_source_unavailable"),
                Inherits: null,
                BindsTwoWayByDefault: null,
                DefaultUpdateSourceTrigger: null,
                IsAnimationProhibited: null,
                Evidence: new ProvenanceEvidence(
                    ProvenanceEvidenceKind.Unavailable,
                    "metadata_unavailable"));
        }

        var frameworkMetadata = metadata as FrameworkPropertyMetadata;
        var canFormatDefaultValue = CanFormatProvenanceValueExactly(metadata.DefaultValue, valueFormat);
        return new DefaultMetadataPropertyProvenance(
            DefaultValue: canFormatDefaultValue
                ? FormatSafeProvenanceValue(metadata.DefaultValue, valueFormat, maxStringLength)
                : null,
            DefaultValueType: GetTypeName(metadata.DefaultValue),
            DefaultValueEvidence: canFormatDefaultValue
                ? new ProvenanceEvidence(ProvenanceEvidenceKind.Exact)
                : new ProvenanceEvidence(
                    ProvenanceEvidenceKind.Unavailable,
                    "value_not_safely_serializable"),
            MetadataType: GetTypeName(metadata) ?? metadata.GetType().Name,
            IsEffectiveValueSource: isEffectiveValueSource,
            EffectiveValueSourceEvidence: isEffectiveValueSource.HasValue
                ? new ProvenanceEvidence(ProvenanceEvidenceKind.Exact)
                : new ProvenanceEvidence(
                    ProvenanceEvidenceKind.Unavailable,
                    "value_source_unavailable"),
            Inherits: frameworkMetadata?.Inherits,
            BindsTwoWayByDefault: frameworkMetadata?.BindsTwoWayByDefault,
            DefaultUpdateSourceTrigger: frameworkMetadata?.DefaultUpdateSourceTrigger.ToString(),
            IsAnimationProhibited: frameworkMetadata?.IsAnimationProhibited,
            Evidence: new ProvenanceEvidence(ProvenanceEvidenceKind.Exact));
    }

    private static string DescribeDelegate(Delegate callback)
    {
        var method = callback.Method;
        var declaringType = method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "unknown";
        return TruncateProvenanceText($"{declaringType}.{method.Name}", 512);
    }

    private static bool CanFormatProvenanceValueExactly(object? value, string valueFormat)
    {
        if (value is null || ReferenceEquals(value, DependencyProperty.UnsetValue))
        {
            return true;
        }

        if (string.Equals(valueFormat, "type", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var type = value.GetType();
        return IsSafeScalarType(type) || value is Type or SolidColorBrush;
    }

    private static string? FormatSafeResourceKey(object? key)
    {
        if (key is null)
        {
            return null;
        }

        if (key is ComponentResourceKey componentKey)
        {
            if (componentKey.ResourceId is null || !IsSafeResourceKey(componentKey.ResourceId))
            {
                return null;
            }

            var type = GetTypeName(componentKey.TypeInTargetAssembly) ?? "unknown";
            var id = FormatSafeProvenanceValue(componentKey.ResourceId, "string", 200) ?? "unknown";
            return TruncateProvenanceText($"ComponentResourceKey({type}, {id})", 512);
        }

        return IsSafeResourceKey(key)
            ? FormatSafeProvenanceValue(key, "string", 200)
            : null;
    }

    private static string? FormatSafeProvenanceValue(object? value, string valueFormat, int maxLength)
    {
        if (value is null)
        {
            return "null";
        }

        if (ReferenceEquals(value, DependencyProperty.UnsetValue))
        {
            return "{UnsetValue}";
        }

        var type = value.GetType();
        var typeName = type.FullName ?? type.Name;
        if (string.Equals(valueFormat, "type", StringComparison.OrdinalIgnoreCase))
        {
            return TruncateProvenanceText(typeName, maxLength);
        }

        string? text = value switch
        {
            string stringValue => stringValue,
            char character => character.ToString(),
            bool boolean => boolean ? "true" : "false",
            byte number => number.ToString(CultureInfo.InvariantCulture),
            sbyte number => number.ToString(CultureInfo.InvariantCulture),
            short number => number.ToString(CultureInfo.InvariantCulture),
            ushort number => number.ToString(CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            uint number => number.ToString(CultureInfo.InvariantCulture),
            long number => number.ToString(CultureInfo.InvariantCulture),
            ulong number => number.ToString(CultureInfo.InvariantCulture),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            Enum enumeration => enumeration.ToString(),
            Guid guid => guid.ToString("D"),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
            Type reflectedType => reflectedType.FullName ?? reflectedType.Name,
            SolidColorBrush brush => $"SolidColorBrush(#{brush.Color.A:X2}{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2})",
            _ => null
        };

        if (text is null && value is FrameworkElement frameworkElement)
        {
            text = DescribeBindingRuntimeSource(frameworkElement, maxLength);
        }

        return TruncateProvenanceText(text ?? typeName, maxLength);
    }

    internal static string TruncateProvenanceText(string value, int maxLength)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        if (maxLength <= 3)
        {
            return new string('.', maxLength);
        }

        var length = maxLength - 3;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length] + "...";
    }

    private static string? GetTypeName(object? value)
    {
        var name = value switch
        {
            null => null,
            Type type => type.FullName ?? type.Name,
            _ => value.GetType().FullName ?? value.GetType().Name
        };

        return name is null ? null : TruncateProvenanceText(name, 512);
    }
}
