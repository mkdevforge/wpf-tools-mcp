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
    private static readonly Type RuntimeTypeImplementation = typeof(object).GetType();

    private const string WinningStyleContributorUnavailable = "style_winner_not_exposed";

    private const string WinningTemplateContributorUnavailable = "template_winner_not_exposed";

    private const string StaticResourceOriginUnavailable = "static_resource_origin_not_retained";

    // Public ResourceDictionary enumeration copies every key and realizes values.
    // Guarded raw storage lets one bucket consume exactly one provenance scan unit.
    private static readonly FieldInfo? ResourceDictionaryBaseDictionaryField =
        typeof(ResourceDictionary).GetField("_baseDictionary", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? ResourceDictionaryMergedDictionariesField =
        typeof(ResourceDictionary).GetField("_mergedDictionaries", BindingFlags.Instance | BindingFlags.NonPublic);

    // WPF Resources getters lazily install collections. Read only existing backing storage.
    private static readonly object? FrameworkElementResourcesField = GetStaticFieldValue(
        typeof(FrameworkElement),
        "ResourcesField");

    private static readonly object? FrameworkContentElementResourcesField = GetStaticFieldValue(
        typeof(FrameworkContentElement),
        "ResourcesField");

    private static readonly MethodInfo? FrameworkElementResourcesFieldGetValueMethod =
        GetUncommonResourceFieldGetValueMethod(FrameworkElementResourcesField);

    private static readonly MethodInfo? FrameworkContentElementResourcesFieldGetValueMethod =
        GetUncommonResourceFieldGetValueMethod(FrameworkContentElementResourcesField);

    private static readonly FieldInfo? StyleResourcesField =
        typeof(Style).GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? FrameworkTemplateResourcesField =
        typeof(FrameworkTemplate).GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? ApplicationResourcesField =
        typeof(Application).GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly object? ApplicationGlobalLock = GetStaticFieldValue(
        typeof(Application),
        "_globalLock");

    private enum ResourceOwnerStorageAccess
    {
        Absent,
        Present,
        Unavailable
    }

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
            ? MapValueSource(
                exactSource.BaseValueSource,
                exactSource.IsExpression,
                exactSource.IsAnimated,
                exactSource.IsCoerced,
                exactSource.IsCurrent)
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

    internal static DependencyPropertyValueSourceProvenance MapValueSource(
        BaseValueSource source,
        bool isExpression,
        bool isAnimated,
        bool isCoerced,
        bool isCurrent) =>
        new(
            MapBaseValueSource(source),
            isExpression,
            isAnimated,
            isCoerced,
            isCurrent,
            new ProvenanceEvidence(ProvenanceEvidenceKind.Exact));

    internal static DependencyPropertyBaseValueSource MapBaseValueSource(BaseValueSource source) => source switch
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

    internal static BindingProvenance BuildBindingProvenance(
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
        var textTruncated = false;

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
                out converter,
                out textTruncated);
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
            converter = GetTypeName(binding.Converter, ref textTruncated);
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

            var childProvenance = BuildBindingChildProvenance(
                child,
                i,
                metadata,
                expression is MultiBindingExpression ? parentMode : null,
                expression is MultiBindingExpression ? parentUpdateSourceTrigger : null,
                maxStringLength,
                out var childTextTruncated);
            textTruncated |= childTextTruncated;
            returnedChildren.Add(childProvenance);
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
            Evidence: textTruncated
                ? new ProvenanceEvidence(
                    ProvenanceEvidenceKind.BestEffort,
                    "maxStringLength")
                : new ProvenanceEvidence(ProvenanceEvidenceKind.Exact));
    }

    private static BindingChildProvenance BuildBindingChildProvenance(
        BindingExpressionBase expression,
        int index,
        PropertyMetadata? metadata,
        BindingMode? parentMode,
        UpdateSourceTrigger? parentUpdateSourceTrigger,
        int maxStringLength,
        out bool textTruncated)
    {
        textTruncated = false;
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
                out converter,
                out textTruncated);
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
        out string? converter,
        out bool textTruncated)
    {
        textTruncated = false;
        var binding = expression.ParentBinding;
        path = TruncateBindingText(
            binding.Path?.Path ?? binding.XPath ?? string.Empty,
            maxStringLength,
            ref textTruncated);
        if (path.Length == 0)
        {
            path = null;
        }

        (sourceKind, sourceSummary) = DescribeConfiguredBindingSource(
            binding,
            maxStringLength,
            out var configuredSourceTruncated);
        textTruncated |= configuredSourceTruncated;
        dataItemSummary = DescribeBindingRuntimeSource(
            expression.DataItem,
            maxStringLength,
            out var dataItemTruncated);
        textTruncated |= dataItemTruncated;
        resolvedSourceSummary = DescribeBindingRuntimeSource(
            expression.ResolvedSource,
            maxStringLength,
            out var resolvedSourceTruncated);
        textTruncated |= resolvedSourceTruncated;
        resolvedSourcePropertyName = string.IsNullOrEmpty(expression.ResolvedSourcePropertyName)
            ? null
            : TruncateBindingText(
                expression.ResolvedSourcePropertyName,
                maxStringLength,
                ref textTruncated);
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
        converter = GetTypeName(binding.Converter, ref textTruncated);
    }

    private static (string Kind, string Summary) DescribeConfiguredBindingSource(
        Binding binding,
        int maxStringLength,
        out bool textTruncated)
    {
        textTruncated = false;
        if (binding.Source is not null)
        {
            return (
                "ExplicitSource",
                DescribeBindingRuntimeSource(binding.Source, maxStringLength, out textTruncated) ?? "null");
        }

        if (!string.IsNullOrWhiteSpace(binding.ElementName))
        {
            return (
                "ElementName",
                TruncateBindingText(binding.ElementName, maxStringLength, ref textTruncated));
        }

        if (binding.RelativeSource is { } relativeSource)
        {
            var summary = relativeSource.Mode.ToString();
            if (relativeSource.Mode == RelativeSourceMode.FindAncestor)
            {
                summary += $" ancestorType={GetTypeName(relativeSource.AncestorType, ref textTruncated) ?? "unknown"}";
                summary += $" level={relativeSource.AncestorLevel}";
            }

            return (
                "RelativeSource",
                TruncateBindingText(summary, maxStringLength, ref textTruncated));
        }

        return ("DataContext", "Inherited or local DataContext");
    }

    private static string? DescribeBindingRuntimeSource(object? source, int maxStringLength) =>
        DescribeBindingRuntimeSource(source, maxStringLength, out _);

    private static string? DescribeBindingRuntimeSource(
        object? source,
        int maxStringLength,
        out bool textTruncated)
    {
        textTruncated = false;
        if (source is null)
        {
            return null;
        }

        if (ReferenceEquals(source, BindingOperations.DisconnectedSource))
        {
            return "{DisconnectedSource}";
        }

        var typeName = GetTypeName(source, ref textTruncated) ?? "unknown";
        if (source is FrameworkElement frameworkElement)
        {
            var name = frameworkElement.Name;
            var automationId = AutomationProperties.GetAutomationId(frameworkElement);
            if (!string.IsNullOrWhiteSpace(automationId))
            {
                return TruncateBindingText(
                    $"{typeName} automationId={automationId}",
                    maxStringLength,
                    ref textTruncated);
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                return TruncateBindingText(
                    $"{typeName} name={name}",
                    maxStringLength,
                    ref textTruncated);
            }
        }

        return TruncateBindingText(typeName, maxStringLength, ref textTruncated);
    }

    private static string TruncateBindingText(string value, int maxLength, ref bool textTruncated)
    {
        textTruncated |= value.Length > Math.Max(0, maxLength);
        return TruncateProvenanceText(value, maxLength);
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
                    conditionsEvidence: null,
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
        DataTemplate dataTemplate => FormatProvenanceValueBestEffort(dataTemplate.DataType, "string", 200),
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
        ProvenanceEvidence? conditionsEvidence,
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
            var formattedValue = FormatProvenanceValueWithEvidence(
                setter.Value,
                "string",
                maxStringLength);
            candidates.Add(new PropertyContributorCandidate(
                Kind: candidateKind,
                DeclaringType: declaringType,
                TargetName: string.IsNullOrWhiteSpace(setter.TargetName)
                    ? null
                    : TruncateProvenanceText(setter.TargetName, maxStringLength),
                Value: formattedValue.Value,
                Conditions: conditions,
                Evidence: new ProvenanceEvidence(
                    ProvenanceEvidenceKind.BestEffort,
                    candidateKind == "TemplateTrigger"
                        ? WinningTemplateContributorUnavailable
                        : WinningStyleContributorUnavailable))
            {
                ValueEvidence = formattedValue.Evidence,
                ConditionsEvidence = conditionsEvidence
            });
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

            var conditions = DescribeTriggerConditions(trigger, maxStringLength);
            ScanSetterCollection(
                setters,
                property,
                candidateKind,
                declaringType,
                conditions.Text,
                conditions.Evidence,
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

    private static (string Text, ProvenanceEvidence Evidence) DescribeTriggerConditions(
        TriggerBase trigger,
        int maxStringLength)
    {
        if (trigger is Trigger propertyTrigger)
        {
            var formatted = FormatProvenanceValueWithEvidence(
                propertyTrigger.Value,
                "string",
                maxStringLength);
            return BuildTriggerCondition(
                $"{GetTypeName(propertyTrigger.Property?.OwnerType) ?? "unknown"}.{propertyTrigger.Property?.Name} == ",
                formatted,
                maxStringLength);
        }

        if (trigger is DataTrigger dataTrigger)
        {
            var formatted = FormatProvenanceValueWithEvidence(
                dataTrigger.Value,
                "string",
                maxStringLength);
            return BuildTriggerCondition(
                $"Binding({DescribeBindingPath(dataTrigger.Binding)}) == ",
                formatted,
                maxStringLength);
        }

        var description = trigger switch
        {
            MultiTrigger multiTrigger => $"{multiTrigger.Conditions.Count} property conditions",
            MultiDataTrigger multiDataTrigger => $"{multiDataTrigger.Conditions.Count} binding conditions",
            _ => GetTypeName(trigger) ?? "unknown"
        };

        return (
            TruncateProvenanceText(description, maxStringLength),
            description.Length > Math.Max(0, maxStringLength)
                ? new ProvenanceEvidence(ProvenanceEvidenceKind.BestEffort, "maxStringLength")
                : new ProvenanceEvidence(ProvenanceEvidenceKind.Exact));
    }

    private static (string Text, ProvenanceEvidence Evidence) BuildTriggerCondition(
        string prefix,
        (string? Value, ProvenanceEvidence Evidence) formatted,
        int maxStringLength)
    {
        var description = prefix + formatted.Value;
        var text = TruncateProvenanceText(description, maxStringLength);
        var evidence = description.Length > Math.Max(0, maxStringLength) &&
                       formatted.Evidence.Kind != ProvenanceEvidenceKind.Unavailable
            ? new ProvenanceEvidence(ProvenanceEvidenceKind.BestEffort, "maxStringLength")
            : formatted.Evidence;

        return (text, evidence);
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
                var resourceDictionaries = GetResourceDictionaries(parent, out var ownerStorageUnavailable);
                if (ownerStorageUnavailable)
                {
                    scanIncompleteReason ??= "resource_owner_storage_unavailable";
                }

                foreach (var (dictionary, suffix) in resourceDictionaries)
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

            if (!budget.Exhausted && Application.Current is { } application)
            {
                var applicationResourceAccess = GetExistingResourcesForProvenance(
                    application,
                    out var applicationResources);
                if (applicationResourceAccess == ResourceOwnerStorageAccess.Unavailable)
                {
                    scanIncompleteReason ??= "resource_owner_storage_unavailable";
                }
                else if (applicationResourceAccess == ResourceOwnerStorageAccess.Present && budget.TryConsume())
                {
                    ProbeResourceDictionary(
                        applicationResources,
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
        }

        var scanComplete = !budget.Exhausted && scanIncompleteReason is null;

        var referenceKind = dynamicResourceDetected
            ? "DynamicResource"
            : candidates.Count > 0
                ? "ResourceCandidate"
                : "Unknown";
        var dynamicResourceKeyFormatting = dynamicResourceDetected
            ? FormatResourceKeyDetails(dynamicResourceKey)
            : default;
        var singleCandidate = scanComplete && candidates.Count == 1
            ? candidates[0]
            : null;
        var key = dynamicResourceDetected
            ? dynamicResourceKeyFormatting.Text
            : singleCandidate?.Key;
        var scope = scanComplete && candidates.Count == 1 ? candidates[0].Scope : null;
        var keyEvidence = dynamicResourceDetected
            ? BuildResourceKeyEvidence(dynamicResourceKeyFormatting, "dynamic_resource_internal_expression")
            : singleCandidate is not null
                ? singleCandidate.Evidence
                : new ProvenanceEvidence(
                    ProvenanceEvidenceKind.Unavailable,
                    StaticResourceOriginUnavailable);
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

    private static IReadOnlyList<(ResourceDictionary Dictionary, string ScopeSuffix)> GetResourceDictionaries(
        DependencyObject element,
        out bool storageUnavailable)
    {
        var dictionaries = new List<(ResourceDictionary Dictionary, string ScopeSuffix)>(capacity: 4);
        storageUnavailable = false;

        if (element is FrameworkElement frameworkElement)
        {
            AddExistingResourceDictionary(frameworkElement, ".Resources", dictionaries, ref storageUnavailable);

            if (frameworkElement.Style is { } style)
            {
                AddExistingResourceDictionary(style, ".Style.Resources", dictionaries, ref storageUnavailable);
            }

            if (TryGetAppliedTemplate(frameworkElement) is { } template)
            {
                AddExistingResourceDictionary(template, ".Template.Resources", dictionaries, ref storageUnavailable);
            }

            if (TryGetRelevantStyle(frameworkElement, StyleProvenanceKind.Theme) is { } themeStyle)
            {
                AddExistingResourceDictionary(
                    themeStyle,
                    ".ThemeStyle.Resources",
                    dictionaries,
                    ref storageUnavailable);
            }
        }
        else if (element is FrameworkContentElement contentElement)
        {
            AddExistingResourceDictionary(contentElement, ".Resources", dictionaries, ref storageUnavailable);

            if (contentElement.Style is { } style)
            {
                AddExistingResourceDictionary(style, ".Style.Resources", dictionaries, ref storageUnavailable);
            }

            if (TryGetRelevantStyle(contentElement, StyleProvenanceKind.Theme) is { } themeStyle)
            {
                AddExistingResourceDictionary(
                    themeStyle,
                    ".ThemeStyle.Resources",
                    dictionaries,
                    ref storageUnavailable);
            }
        }

        return dictionaries;
    }

    internal static bool TryGetExistingResourcesForProvenance(
        object owner,
        out ResourceDictionary resources) =>
        GetExistingResourcesForProvenance(owner, out resources) == ResourceOwnerStorageAccess.Present;

    private static void AddExistingResourceDictionary(
        object owner,
        string scopeSuffix,
        List<(ResourceDictionary Dictionary, string ScopeSuffix)> dictionaries,
        ref bool storageUnavailable)
    {
        var access = GetExistingResourcesForProvenance(owner, out var resources);
        if (access == ResourceOwnerStorageAccess.Present)
        {
            dictionaries.Add((resources, scopeSuffix));
        }
        else if (access == ResourceOwnerStorageAccess.Unavailable)
        {
            storageUnavailable = true;
        }
    }

    private static ResourceOwnerStorageAccess GetExistingResourcesForProvenance(
        object owner,
        out ResourceDictionary resources)
    {
        resources = null!;

        try
        {
            object? value;
            switch (owner)
            {
                case FrameworkElement frameworkElement:
                    if (FrameworkElementResourcesField is null ||
                        FrameworkElementResourcesFieldGetValueMethod is null)
                    {
                        return ResourceOwnerStorageAccess.Unavailable;
                    }

                    value = GetUncommonResourceFieldValue(
                        FrameworkElementResourcesField,
                        FrameworkElementResourcesFieldGetValueMethod,
                        frameworkElement);
                    break;
                case FrameworkContentElement contentElement:
                    if (FrameworkContentElementResourcesField is null ||
                        FrameworkContentElementResourcesFieldGetValueMethod is null)
                    {
                        return ResourceOwnerStorageAccess.Unavailable;
                    }

                    value = GetUncommonResourceFieldValue(
                        FrameworkContentElementResourcesField,
                        FrameworkContentElementResourcesFieldGetValueMethod,
                        contentElement);
                    break;
                case Style style:
                    if (StyleResourcesField is null)
                    {
                        return ResourceOwnerStorageAccess.Unavailable;
                    }

                    value = StyleResourcesField.GetValue(style);
                    break;
                case FrameworkTemplate template:
                    if (FrameworkTemplateResourcesField is null)
                    {
                        return ResourceOwnerStorageAccess.Unavailable;
                    }

                    value = FrameworkTemplateResourcesField.GetValue(template);
                    break;
                case Application application:
                    if (ApplicationResourcesField is null || ApplicationGlobalLock is null)
                    {
                        return ResourceOwnerStorageAccess.Unavailable;
                    }

                    lock (ApplicationGlobalLock)
                    {
                        value = ApplicationResourcesField.GetValue(application);
                    }

                    break;
                default:
                    return ResourceOwnerStorageAccess.Unavailable;
            }

            if (value is not ResourceDictionary existingResources)
            {
                return ResourceOwnerStorageAccess.Absent;
            }

            resources = existingResources;
            return ResourceOwnerStorageAccess.Present;
        }
        catch
        {
            return ResourceOwnerStorageAccess.Unavailable;
        }
    }

    private static object? GetUncommonResourceFieldValue(
        object uncommonField,
        MethodInfo getValueMethod,
        DependencyObject owner) =>
        getValueMethod.Invoke(uncommonField, [owner]);

    private static object? GetStaticFieldValue(Type type, string fieldName)
    {
        try
        {
            return type
                .GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static MethodInfo? GetUncommonResourceFieldGetValueMethod(object? uncommonField)
    {
        try
        {
            return uncommonField?
                .GetType()
                .GetMethod(
                    "GetValue",
                    BindingFlags.Instance | BindingFlags.Public,
                    binder: null,
                    types: [typeof(DependencyObject)],
                    modifiers: null);
        }
        catch
        {
            return null;
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

        var canMatchKnownKey = hasKnownKey && knownKey is not null;
        for (var i = 0; buckets is not null && i < buckets.Length; i++)
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
            if (canMatchKnownKey)
            {
                if (AreResourceKeysEqualBestEffort(
                        key,
                        knownKey!,
                        ref scanIncompleteReason))
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

            if (hasEffectiveValue &&
                AreResourceCandidateValuesEqualBestEffort(
                    storedValue,
                    effectiveValue,
                    ref scanIncompleteReason))
            {
                AddResourceCandidate(
                    key,
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
        out Array? buckets)
    {
        buckets = null;
        if (ResourceDictionaryBaseDictionaryField is null ||
            HashtableBucketsField is null ||
            HashtableBucketKeyField is null ||
            HashtableBucketValueField is null)
        {
            return false;
        }

        try
        {
            var rawDictionary = ResourceDictionaryBaseDictionaryField.GetValue(dictionary);
            if (rawDictionary is null)
            {
                return true;
            }

            if (rawDictionary is not Hashtable baseDictionary ||
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

        var formattedKey = FormatResourceKeyDetails(key);
        candidates.Add(new ResourceCandidateProvenance(
            Key: formattedKey.Text,
            Scope: TruncateProvenanceText(scope, 512),
            Evidence: BuildResourceKeyEvidence(formattedKey, "resource_reference_candidate")));
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

    internal static bool AreResourceCandidateValuesEqualBestEffort(
        object? candidate,
        object? effectiveValue,
        ref string? scanIncompleteReason)
    {
        if (ReferenceEquals(candidate, effectiveValue))
        {
            return true;
        }

        if (candidate is null || effectiveValue is null || candidate.GetType() != effectiveValue.GetType())
        {
            return false;
        }

        try
        {
            return candidate.Equals(effectiveValue);
        }
        catch (Exception ex)
        {
            scanIncompleteReason ??= TruncateProvenanceText(
                $"resource_value_comparison_failed:{ex.GetType().FullName ?? ex.GetType().Name}",
                512);
            return false;
        }
    }

    internal static bool AreResourceKeysEqualBestEffort(
        object candidate,
        object expected,
        ref string? scanIncompleteReason)
    {
        if (ReferenceEquals(candidate, expected))
        {
            return true;
        }

        if (candidate.GetType() != expected.GetType())
        {
            return false;
        }

        try
        {
            return candidate.Equals(expected);
        }
        catch (Exception ex)
        {
            scanIncompleteReason ??= TruncateProvenanceText(
                $"resource_key_comparison_failed:{ex.GetType().FullName ?? ex.GetType().Name}",
                512);
            return false;
        }
    }

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
                var formattedBaseValue = FormatProvenanceValueWithEvidence(
                    baseValue,
                    valueFormat,
                    maxStringLength);
                return new AnimationPropertyProvenance(
                    BaseValue: formattedBaseValue.Value,
                    BaseValueType: GetTypeName(baseValue),
                    BaseValueEvidence: formattedBaseValue.Evidence,
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
        var formattedDefaultValue = FormatProvenanceValueWithEvidence(
            metadata.DefaultValue,
            valueFormat,
            maxStringLength);
        return new DefaultMetadataPropertyProvenance(
            DefaultValue: formattedDefaultValue.Value,
            DefaultValueType: GetTypeName(metadata.DefaultValue),
            DefaultValueEvidence: formattedDefaultValue.Evidence,
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

    internal static BestEffortProvenanceValueFormatting FormatResourceKeyDetails(object? key)
    {
        if (key is null)
        {
            return new BestEffortProvenanceValueFormatting(
                Text: null,
                RepresentsValue: false,
                Truncated: false);
        }

        if (key is ComponentResourceKey componentKey)
        {
            try
            {
                var formattedType = componentKey.TypeInTargetAssembly is { } targetType
                    ? FormatRepresentedTypeNameBestEffort(targetType, 512)
                    : new BestEffortProvenanceValueFormatting(
                        Text: "unknown",
                        RepresentsValue: false,
                        Truncated: false,
                        FormattingFailureReason: "type_name_unavailable");
                if (componentKey.ResourceId is null)
                {
                    return FormatComponentResourceKey(
                        formattedType,
                        resourceId: null);
                }

                var id = FormatProvenanceValueBestEffortDetails(componentKey.ResourceId, "string", 200);
                return FormatComponentResourceKey(formattedType, id);
            }
            catch (Exception formattingFailure)
            {
                return CreateUnavailableProvenanceValueFormatting(componentKey, formattingFailure);
            }
        }

        return FormatProvenanceValueBestEffortDetails(key, "string", 200);
    }

    private static BestEffortProvenanceValueFormatting FormatComponentResourceKey(
        BestEffortProvenanceValueFormatting formattedType,
        BestEffortProvenanceValueFormatting? resourceId)
    {
        var rawText = $"ComponentResourceKey({formattedType.Text ?? "unknown"}, " +
            $"{resourceId?.Text ?? "null"})";
        var failedPart = !formattedType.RepresentsValue
            ? formattedType
            : resourceId is { RepresentsValue: false } failedResourceId
                ? failedResourceId
                : default(BestEffortProvenanceValueFormatting?);
        return new BestEffortProvenanceValueFormatting(
            Text: TruncateProvenanceText(rawText, 512),
            RepresentsValue: failedPart is null,
            Truncated: formattedType.Truncated ||
                resourceId?.Truncated is true ||
                rawText.Length > 512,
            BestEffortReason: formattedType.BestEffortReason ?? resourceId?.BestEffortReason,
            FormattingFailureType: failedPart?.FormattingFailureType,
            FormattingFailureReason: failedPart?.FormattingFailureReason);
    }

    internal readonly record struct BestEffortProvenanceValueFormatting(
        string? Text,
        bool RepresentsValue,
        bool Truncated,
        string? BestEffortReason = null,
        string? FormattingFailureType = null,
        string? FormattingFailureReason = null);

    internal static (string? Value, ProvenanceEvidence Evidence) FormatProvenanceValueWithEvidence(
        object? value,
        string valueFormat,
        int maxLength)
    {
        var formatted = FormatProvenanceValueBestEffortDetails(value, valueFormat, maxLength);
        if (!formatted.RepresentsValue)
        {
            return (
                formatted.Text,
                new ProvenanceEvidence(
                    ProvenanceEvidenceKind.Unavailable,
                    BuildFormattingFailureReason(
                        formatted.FormattingFailureReason ?? "value_to_string_failed",
                        formatted.FormattingFailureType)));
        }

        if (formatted.Truncated)
        {
            return (
                formatted.Text,
                new ProvenanceEvidence(
                    ProvenanceEvidenceKind.BestEffort,
                    "maxStringLength"));
        }

        return formatted.BestEffortReason is not null
            ? (
                formatted.Text,
                new ProvenanceEvidence(
                    ProvenanceEvidenceKind.BestEffort,
                    formatted.BestEffortReason))
            : (formatted.Text, new ProvenanceEvidence(ProvenanceEvidenceKind.Exact));
    }

    private static string? FormatProvenanceValueBestEffort(object? value, string valueFormat, int maxLength) =>
        FormatProvenanceValueBestEffortDetails(value, valueFormat, maxLength).Text;

    internal static BestEffortProvenanceValueFormatting FormatProvenanceValueBestEffortDetails(
        object? value,
        string valueFormat,
        int maxLength)
    {
        if (value is null)
        {
            return CreateProvenanceValueFormatting("null", representsValue: true, maxLength);
        }

        if (ReferenceEquals(value, DependencyProperty.UnsetValue))
        {
            return CreateProvenanceValueFormatting("{UnsetValue}", representsValue: true, maxLength);
        }

        if (value is Type representedType)
        {
            return FormatRepresentedTypeNameBestEffort(representedType, maxLength);
        }

        var type = value.GetType();
        var typeName = type.FullName ?? type.Name;
        if (string.Equals(valueFormat, "type", StringComparison.OrdinalIgnoreCase))
        {
            return CreateProvenanceValueFormatting(typeName, representsValue: true, maxLength);
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
            Thickness thickness => FormatThickness(thickness),
            CornerRadius cornerRadius => FormatCornerRadius(cornerRadius),
            GridLength gridLength => FormatGridLength(gridLength),
            Point point => $"{FormatInvariantDouble(point.X)},{FormatInvariantDouble(point.Y)}",
            Size size => $"{FormatInvariantDouble(size.Width)},{FormatInvariantDouble(size.Height)}",
            System.Windows.Rect rect => rect.IsEmpty
                ? "Empty"
                : $"{FormatInvariantDouble(rect.X)},{FormatInvariantDouble(rect.Y)},{FormatInvariantDouble(rect.Width)},{FormatInvariantDouble(rect.Height)}",
            Vector vector => $"{FormatInvariantDouble(vector.X)},{FormatInvariantDouble(vector.Y)}",
            Int32Rect int32Rect => $"{int32Rect.X},{int32Rect.Y},{int32Rect.Width},{int32Rect.Height}",
            Matrix matrix => string.Join(",",
                FormatInvariantDouble(matrix.M11),
                FormatInvariantDouble(matrix.M12),
                FormatInvariantDouble(matrix.M21),
                FormatInvariantDouble(matrix.M22),
                FormatInvariantDouble(matrix.OffsetX),
                FormatInvariantDouble(matrix.OffsetY)),
            Color color => FormatColor(color),
            SolidColorBrush brush when type == typeof(SolidColorBrush) => FormatColor(brush.Color),
            FontFamily fontFamily when type == typeof(FontFamily) => fontFamily.Source,
            FontWeight fontWeight => fontWeight.ToString(),
            FontStyle fontStyle => fontStyle.ToString(),
            FontStretch fontStretch => fontStretch.ToString(),
            Duration duration => FormatDuration(duration),
            RepeatBehavior repeatBehavior => FormatRepeatBehavior(repeatBehavior),
            Point3D point3D => string.Join(",",
                FormatInvariantDouble(point3D.X),
                FormatInvariantDouble(point3D.Y),
                FormatInvariantDouble(point3D.Z)),
            Vector3D vector3D => string.Join(",",
                FormatInvariantDouble(vector3D.X),
                FormatInvariantDouble(vector3D.Y),
                FormatInvariantDouble(vector3D.Z)),
            Quaternion quaternion => string.Join(",",
                FormatInvariantDouble(quaternion.X),
                FormatInvariantDouble(quaternion.Y),
                FormatInvariantDouble(quaternion.Z),
                FormatInvariantDouble(quaternion.W)),
            _ => null
        };

        if (text is not null)
        {
            return CreateProvenanceValueFormatting(text, representsValue: true, maxLength);
        }

        if (value is FrameworkElement frameworkElement)
        {
            return CreateProvenanceValueFormatting(
                DescribeBindingRuntimeSource(frameworkElement, maxLength) ?? typeName,
                representsValue: true,
                maxLength,
                bestEffortReason: "runtime_source_summary");
        }

        try
        {
            return CreateProvenanceValueFormatting(
                value.ToString() ?? string.Empty,
                representsValue: true,
                maxLength,
                bestEffortReason: "application_to_string");
        }
        catch (Exception formattingFailure)
        {
            return CreateUnavailableProvenanceValueFormatting(value, formattingFailure, maxLength);
        }
    }

    private static BestEffortProvenanceValueFormatting CreateProvenanceValueFormatting(
        string text,
        bool representsValue,
        int maxLength,
        string? bestEffortReason = null) =>
        new(
            TruncateProvenanceText(text, maxLength),
            representsValue,
            text.Length > Math.Max(0, maxLength),
            bestEffortReason);

    private static BestEffortProvenanceValueFormatting CreateUnavailableProvenanceValueFormatting(
        object value,
        Exception formattingFailure,
        int maxLength = 512) =>
        new(
            TruncateProvenanceText(GetTypeName(value) ?? value.GetType().Name, maxLength),
            RepresentsValue: false,
            Truncated: false,
            FormattingFailureType: TruncateProvenanceText(
                formattingFailure.GetType().FullName ?? formattingFailure.GetType().Name,
                512),
            FormattingFailureReason: "value_to_string_failed");

    internal static ProvenanceEvidence BuildResourceKeyEvidence(
        BestEffortProvenanceValueFormatting formatting,
        string successReason)
    {
        if (!formatting.RepresentsValue)
        {
            var failureReason = string.Equals(
                formatting.FormattingFailureReason,
                "value_to_string_failed",
                StringComparison.Ordinal)
                    ? "resource_key_to_string_failed"
                    : formatting.FormattingFailureReason ?? "resource_key_to_string_failed";
            return new ProvenanceEvidence(
                ProvenanceEvidenceKind.Unavailable,
                formatting.Text is null && formatting.FormattingFailureType is null
                    ? "resource_key_unavailable"
                    : BuildFormattingFailureReason(
                        failureReason,
                        formatting.FormattingFailureType));
        }

        return new ProvenanceEvidence(
            ProvenanceEvidenceKind.BestEffort,
            formatting.Truncated ? "maxStringLength" : successReason);
    }

    private static string BuildFormattingFailureReason(string reason, string? failureType) =>
        failureType is null
            ? reason
            : TruncateProvenanceText($"{reason}:{failureType}", 512);

    private static string FormatInvariantDouble(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static string FormatThickness(Thickness value) => string.Join(",",
        FormatInvariantDouble(value.Left),
        FormatInvariantDouble(value.Top),
        FormatInvariantDouble(value.Right),
        FormatInvariantDouble(value.Bottom));

    private static string FormatCornerRadius(CornerRadius value) => string.Join(",",
        FormatInvariantDouble(value.TopLeft),
        FormatInvariantDouble(value.TopRight),
        FormatInvariantDouble(value.BottomRight),
        FormatInvariantDouble(value.BottomLeft));

    private static string FormatGridLength(GridLength value)
    {
        if (value.IsAuto)
        {
            return "Auto";
        }

        if (!value.IsStar)
        {
            return FormatInvariantDouble(value.Value);
        }

        return value.Value == 1d ? "*" : $"{FormatInvariantDouble(value.Value)}*";
    }

    private static string FormatColor(Color value) =>
        $"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}";

    private static string FormatDuration(Duration value)
    {
        if (value == Duration.Automatic)
        {
            return "Automatic";
        }

        return value == Duration.Forever
            ? "Forever"
            : value.TimeSpan.ToString("c", CultureInfo.InvariantCulture);
    }

    private static string FormatRepeatBehavior(RepeatBehavior value)
    {
        if (value == RepeatBehavior.Forever)
        {
            return "Forever";
        }

        return value.HasCount
            ? $"{FormatInvariantDouble(value.Count)}x"
            : value.Duration.ToString("c", CultureInfo.InvariantCulture);
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

    internal static string? GetTypeName(object? value)
    {
        var truncated = false;
        return GetTypeName(value, ref truncated);
    }

    private static string? GetTypeName(object? value, ref bool textTruncated)
    {
        if (value is null)
        {
            return null;
        }

        if (value is Type representedType)
        {
            var formatted = FormatRepresentedTypeNameBestEffort(representedType, 512);
            textTruncated |= formatted.Truncated;
            return formatted.RepresentsValue ? formatted.Text : null;
        }

        var type = value.GetType();
        var name = type.FullName ?? type.Name;
        textTruncated |= name.Length > 512;
        return TruncateProvenanceText(name, 512);
    }

    private static BestEffortProvenanceValueFormatting FormatRepresentedTypeNameBestEffort(
        Type representedType,
        int maxLength)
    {
        var bestEffortReason = representedType.GetType() == RuntimeTypeImplementation
            ? null
            : "application_type_full_name";
        Exception? formattingFailure = null;
        try
        {
            if (representedType.FullName is { Length: > 0 } fullName)
            {
                return CreateProvenanceValueFormatting(
                    fullName,
                    representsValue: true,
                    maxLength,
                    bestEffortReason);
            }
        }
        catch (Exception ex)
        {
            formattingFailure = ex;
        }

        try
        {
            if (representedType.Name is { Length: > 0 } name)
            {
                return CreateProvenanceValueFormatting(
                    name,
                    representsValue: true,
                    maxLength,
                    bestEffortReason: bestEffortReason is null ? null : "application_type_name");
            }
        }
        catch (Exception ex)
        {
            formattingFailure ??= ex;
        }

        var implementationType = representedType.GetType();
        var fallbackName = implementationType.FullName ?? implementationType.Name;
        return new BestEffortProvenanceValueFormatting(
            Text: TruncateProvenanceText(fallbackName, maxLength),
            RepresentsValue: false,
            Truncated: fallbackName.Length > Math.Max(0, maxLength),
            FormattingFailureType: formattingFailure is null
                ? null
                : TruncateProvenanceText(
                    formattingFailure.GetType().FullName ?? formattingFailure.GetType().Name,
                    512),
            FormattingFailureReason: formattingFailure is null
                ? "type_name_unavailable"
                : "type_name_getter_failed");
    }
}
