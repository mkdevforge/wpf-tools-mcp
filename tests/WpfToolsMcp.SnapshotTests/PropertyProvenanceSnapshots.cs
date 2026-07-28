using NUnit.Framework;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class PropertyProvenanceSnapshots
{
    private McpTestContext _mcp = null!;
    private string _sessionId = string.Empty;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _mcp = await McpTestContext.StartAsync(
            McpServerPaths.FindMcpServerExecutable(),
            toolProfile: "diagnostics");

        var exePath = TestAppPaths.FindProvenanceProbeTestAppExecutable();
        var launch = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = Path.GetDirectoryName(exePath)!
        });
        _sessionId = launch.SessionId;

        try
        {
            _ = await _mcp.CallToolAsync<InjectAgentResponse>("inject_agent", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId
            });
        }
        catch (InvalidOperationException ex) when (ShouldSkipForMissingAssets(ex))
        {
            Assert.Ignore(ex.Message);
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_mcp is null)
        {
            return;
        }

        try
        {
            _ = await _mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["force"] = true,
                ["timeoutMs"] = 2000
            });
        }
        catch
        {
        }

        await _mcp.DisposeAsync();
    }

    [Test]
    public async Task Provenance_distinguishes_local_default_inherited_and_bound_values()
    {
        var local = await GetPropertyAsync("Provenance_Local", "Width");
        var defaultValue = await GetPropertyAsync("Provenance_Default", "MinWidth");
        var inherited = await GetPropertyAsync("Provenance_Inherited", "Foreground");
        var bound = await GetPropertyAsync("Provenance_Bound", "Text");

        Assert.Multiple(() =>
        {
            Assert.That(local.Provenance!.ValueSource.BaseValueSource, Is.EqualTo(DependencyPropertyBaseValueSource.Local));
            Assert.That(local.Provenance.ValueSource.IsExpression, Is.False);

            Assert.That(defaultValue.Provenance!.ValueSource.BaseValueSource, Is.EqualTo(DependencyPropertyBaseValueSource.Default));
            Assert.That(defaultValue.Provenance.DefaultMetadata.IsEffectiveValueSource, Is.True);
            Assert.That(defaultValue.Provenance.DefaultMetadata.Evidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.Exact));

            Assert.That(inherited.Provenance!.ValueSource.BaseValueSource, Is.EqualTo(DependencyPropertyBaseValueSource.Inherited));
            Assert.That(inherited.Provenance.Inheritance!.ParticipationEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.Exact));
            Assert.That(inherited.Provenance.Inheritance.ProviderSummary, Is.Null);
            Assert.That(inherited.Provenance.Inheritance.ProviderEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.Unavailable));

            Assert.That(bound.Provenance!.ValueSource.BaseValueSource, Is.EqualTo(DependencyPropertyBaseValueSource.Local));
            Assert.That(bound.Provenance.ValueSource.IsExpression, Is.True);
            Assert.That(bound.Provenance.Binding!.Kind, Is.EqualTo("Binding"));
            Assert.That(bound.Provenance.Binding.Path, Is.EqualTo("BoundText"));
            Assert.That(bound.Provenance.Binding.SourceKind, Is.EqualTo("DataContext"));
            Assert.That(bound.Provenance.Binding.DataItemSummary, Does.Contain("ProvenanceViewModel"));
            Assert.That(bound.Provenance.Binding.ResolvedSourceSummary, Does.Contain("ProvenanceViewModel"));
            Assert.That(bound.Provenance.Binding.Mode, Is.EqualTo("Default"));
            Assert.That(bound.Provenance.Binding.EffectiveMode, Is.EqualTo("OneWay"));
            Assert.That(bound.Provenance.Binding.UpdateSourceTrigger, Is.EqualTo("Default"));
            Assert.That(bound.Provenance.Binding.EffectiveUpdateSourceTrigger, Is.EqualTo("PropertyChanged"));
            Assert.That(bound.Provenance.Binding.Converter,
                Is.EqualTo("WpfToolsMcp.TestApp.ProvenanceProbe.ProvenanceConverter"));
            Assert.That(bound.Provenance.Binding.Status, Is.EqualTo("Active"));
            Assert.That(bound.Provenance.Binding.Evidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.Exact));
        });
    }

    [Test]
    public async Task Provenance_reports_style_resource_and_template_candidates_conservatively()
    {
        var styleSetter = await GetPropertyAsync("Provenance_Styled", "FontSize");
        var styleTrigger = await GetPropertyAsync("Provenance_Styled", "FontWeight");
        var styleResource = await GetPropertyAsync("Provenance_Styled", "Foreground", maxProvenanceCandidates: 50);
        var staticResource = await GetPropertyAsync("Provenance_StaticResource", "Background", maxProvenanceCandidates: 50);
        var dynamicResource = await GetPropertyAsync("Provenance_DynamicResource", "Background", maxProvenanceCandidates: 50);
        var ambiguousResource = await GetPropertyAsync("Provenance_AmbiguousResource", "Width", maxProvenanceCandidates: 50);
        var unsafeDynamicResource = await GetPropertyAsync("Provenance_UnsafeDynamicResource", "Background", maxProvenanceCandidates: 50);
        var implicitStyle = await GetPropertyAsync("Provenance_ImplicitStyle", "Padding");
        var themeStyle = await GetPropertyAsync("Provenance_ThemeStyle", "Padding");
        var template = await GetPropertyAsync("Provenance_Template", "Padding");

        Assert.Multiple(() =>
        {
            Assert.That(styleSetter.Provenance!.ValueSource.BaseValueSource, Is.EqualTo(DependencyPropertyBaseValueSource.Style));
            Assert.That(styleSetter.Provenance.Style!.Kind, Is.EqualTo(StyleProvenanceKind.Explicit));
            Assert.That(styleSetter.Provenance.Style.KindEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.Exact));
            Assert.That(styleSetter.Provenance.Style.BasedOnTargetTypes, Is.Not.Empty);
            Assert.That(styleSetter.Provenance.Style.Candidates.Select(c => c.Kind), Does.Contain("StyleSetter"));
            Assert.That(styleSetter.Provenance.Style.ContributorEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.BestEffort));
            Assert.That(styleSetter.Provenance.Style.ResourceKey, Is.Null);
            Assert.That(styleSetter.Provenance.Style.ResourceKeyEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.Unavailable));

            Assert.That(styleTrigger.Provenance!.ValueSource.BaseValueSource, Is.EqualTo(DependencyPropertyBaseValueSource.StyleTrigger));
            Assert.That(styleTrigger.Provenance.Style!.Candidates.Select(c => c.Kind), Does.Contain("StyleTrigger"));
            Assert.That(styleTrigger.Provenance.Style.Candidates.All(c => c.Evidence.Kind == ProvenanceEvidenceKind.BestEffort), Is.True);

            Assert.That(styleResource.Provenance!.Resource!.Candidates.Select(c => c.Key), Does.Contain("Provenance.StaticBrush"));
            Assert.That(styleResource.Provenance.Resource.OriginEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.BestEffort));

            Assert.That(staticResource.Provenance!.Resource!.ReferenceKind, Is.EqualTo("ResourceCandidate"));
            Assert.That(staticResource.Provenance.Resource.Key, Is.EqualTo("Provenance.StaticBrush"));
            Assert.That(staticResource.Provenance.Resource.Scope, Does.EndWith(".Resources"));
            Assert.That(staticResource.Provenance.Resource.ScanComplete, Is.True);
            Assert.That(staticResource.Provenance.Resource.ScanEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.BestEffort));
            Assert.That(staticResource.Provenance.Resource.ScanEvidence.Reason,
                Is.EqualTo("resource_dictionary_internal_access"));
            Assert.That(staticResource.Provenance.Resource.Candidates.Select(c => c.Key), Does.Contain("Provenance.StaticBrush"));
            Assert.That(staticResource.Provenance.Resource.KeyEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.BestEffort));
            Assert.That(staticResource.Provenance.Resource.ScopeEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.BestEffort));
            Assert.That(staticResource.Provenance.Resource.OriginEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.BestEffort));

            Assert.That(dynamicResource.Provenance!.ValueSource.IsExpression, Is.True);
            Assert.That(dynamicResource.Provenance.Resource!.ReferenceKind, Is.EqualTo("DynamicResource"));
            Assert.That(dynamicResource.Provenance.Resource.Key, Is.EqualTo("Provenance.DynamicBrush"));
            Assert.That(dynamicResource.Provenance.Resource.KeyEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.BestEffort));

            Assert.That(ambiguousResource.Provenance!.Resource!.Candidates.Select(c => c.Key),
                Is.SupersetOf(new[] { "Provenance.AmbiguousWidthA", "Provenance.AmbiguousWidthB" }));
            Assert.That(ambiguousResource.Provenance.Resource.Key, Is.Null);
            Assert.That(ambiguousResource.Provenance.Resource.OriginEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.BestEffort));

            Assert.That(unsafeDynamicResource.Provenance!.Resource!.ReferenceKind, Is.EqualTo("DynamicResource"));
            Assert.That(unsafeDynamicResource.Provenance.Resource.Key, Is.Null);
            Assert.That(unsafeDynamicResource.Provenance.Resource.KeyEvidence.Reason,
                Is.EqualTo("dynamic_resource_key_not_safely_serializable"));

            Assert.That(implicitStyle.Provenance!.Style!.Kind, Is.EqualTo(StyleProvenanceKind.Implicit));

            Assert.That(themeStyle.Provenance!.ValueSource.BaseValueSource,
                Is.EqualTo(DependencyPropertyBaseValueSource.DefaultStyle));
            Assert.That(themeStyle.Provenance.Style!.Kind, Is.EqualTo(StyleProvenanceKind.Theme));
            Assert.That(themeStyle.Provenance.Style.StyleDetailsEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.BestEffort));

            Assert.That(template.Provenance!.ValueSource.BaseValueSource, Is.EqualTo(DependencyPropertyBaseValueSource.TemplateTrigger));
            Assert.That(template.Provenance.Template!.ParticipationEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.Exact));
            Assert.That(template.Provenance.Template.Candidates.Select(c => c.Kind), Does.Contain("TemplateTrigger"));
            Assert.That(template.Provenance.Template.ContributorEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.BestEffort));
        });
    }

    [Test]
    public async Task Provenance_reports_the_templated_parent_for_template_created_children()
    {
        var property = await GetPropertyAsync("Provenance_TemplateChild", "Padding");

        Assert.Multiple(() =>
        {
            Assert.That(property.Provenance!.ValueSource.BaseValueSource,
                Is.EqualTo(DependencyPropertyBaseValueSource.ParentTemplate));
            Assert.That(property.Provenance.Template, Is.Not.Null);
            Assert.That(property.Provenance.Template!.Kind, Is.EqualTo("ParentTemplate"));
            Assert.That(property.Provenance.Template.TemplateType,
                Is.EqualTo("System.Windows.Controls.ControlTemplate"));
            Assert.That(property.Provenance.Template.TemplatedParentType,
                Is.EqualTo("System.Windows.Controls.Button"));
            Assert.That(property.Provenance.Template.ParticipationEvidence.Kind,
                Is.EqualTo(ProvenanceEvidenceKind.Exact));
        });
    }

    [Test]
    public async Task Priority_binding_does_not_scan_beyond_the_returned_child_range()
    {
        var property = await GetPropertyAsync(
            "Provenance_PriorityBound",
            "Text",
            maxProvenanceCandidates: 1);

        Assert.Multiple(() =>
        {
            Assert.That(property.Provenance!.Binding!.Kind, Is.EqualTo("PriorityBinding"));
            Assert.That(property.Provenance.Binding.Children, Has.Count.EqualTo(1));
            Assert.That(property.Provenance.Binding.ReturnedChildren, Is.EqualTo(1));
            Assert.That(property.Provenance.Binding.DiscoveredChildren, Is.EqualTo(2));
            Assert.That(property.Provenance.Binding.ScanComplete, Is.True);
            Assert.That(property.Provenance.Binding.ActiveChildIndex, Is.Null);
            Assert.That(property.Provenance.Binding.ActiveChildOutsideReturnedRange, Is.True);
            Assert.That(property.Provenance.Binding.Truncated, Is.True);
        });
    }

    [Test]
    public async Task Provenance_reports_animation_and_coercion_without_inventing_hidden_values()
    {
        var animated = await GetPropertyAsync("Provenance_Animated", "Opacity");
        var coerced = await GetPropertyAsync("Provenance_Coerced", "Level");

        Assert.Multiple(() =>
        {
            Assert.That(animated.Provenance!.ValueSource.IsAnimated, Is.True);
            Assert.That(animated.Provenance.Animation!.BaseValue, Is.EqualTo("0.75"));
            Assert.That(animated.Provenance.Animation.BaseValueEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.Exact));
            Assert.That(animated.Provenance.Animation.OriginEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.Unavailable));

            Assert.That(coerced.Provenance!.ValueSource.IsCoerced, Is.True);
            Assert.That(coerced.Value, Is.EqualTo("100"));
            Assert.That(coerced.Provenance.Coercion!.Callback, Does.Contain("CoercedProbe.CoerceLevel"));
            Assert.That(coerced.Provenance.Coercion.CallbackEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.Exact));
            Assert.That(coerced.Provenance.Coercion.PreCoercionValueEvidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.Unavailable));
        });
    }

    [Test]
    public async Task Provenance_uses_safe_value_summaries_for_application_objects()
    {
        var property = await GetPropertyAsync("Provenance_ThrowingValue", "Payload");

        Assert.Multiple(() =>
        {
            Assert.That(property.Value, Is.EqualTo(
                "WpfToolsMcp.TestApp.ProvenanceProbe.ThrowingDisplayValue"));
            Assert.That(property.ValueType, Is.EqualTo(
                "WpfToolsMcp.TestApp.ProvenanceProbe.ThrowingDisplayValue"));
            Assert.That(property.Provenance!.ValueSource.BaseValueSource,
                Is.EqualTo(DependencyPropertyBaseValueSource.Local));
        });
    }

    [Test]
    public async Task Provenance_candidate_budget_bounds_scan_work_and_reports_completeness()
    {
        var style = await GetPropertyAsync("Provenance_Styled", "FontWeight", maxProvenanceCandidates: 1);
        var resource = await GetPropertyAsync("Provenance_StaticResource", "Background", maxProvenanceCandidates: 1);
        var template = await GetPropertyAsync("Provenance_Template", "Padding", maxProvenanceCandidates: 1);

        Assert.Multiple(() =>
        {
            Assert.That(style.Provenance!.Style!.BasedOnTargetTypes.Count + style.Provenance.Style.ScannedDeclarations, Is.LessThanOrEqualTo(1));
            Assert.That(style.Provenance.Style.ScanComplete, Is.False);
            Assert.That(style.Provenance.Style.TruncatedReason, Is.EqualTo("maxProvenanceCandidates"));

            Assert.That(resource.Provenance!.Resource!.ScannedDictionaries, Is.LessThanOrEqualTo(1));
            Assert.That(resource.Provenance.Resource.ScannedEntries, Is.LessThanOrEqualTo(1));
            Assert.That(resource.Provenance.Resource.ScanAttempts, Is.LessThanOrEqualTo(1));
            Assert.That(resource.Provenance.Resource.ReturnedCandidates, Is.LessThanOrEqualTo(1));
            Assert.That(resource.Provenance.Resource.ScanComplete, Is.False);
            Assert.That(resource.Provenance.Resource.TruncatedReason, Is.EqualTo("maxProvenanceCandidates"));

            Assert.That(template.Provenance!.Template!.ScannedDeclarations, Is.LessThanOrEqualTo(1));
            Assert.That(template.Provenance.Template.ScanComplete, Is.False);
            Assert.That(template.Provenance.Template.TruncatedReason, Is.EqualTo("maxProvenanceCandidates"));
        });
    }

    [Test]
    public async Task Provenance_resource_budget_bounds_a_large_dictionary_before_materializing_it()
    {
        var property = await GetPropertyAsync(
            "Provenance_LargeResourceDictionary",
            "Width",
            maxProvenanceCandidates: 3);

        Assert.Multiple(() =>
        {
            Assert.That(property.Provenance!.Resource!.ScanAttempts, Is.EqualTo(3));
            Assert.That(property.Provenance.Resource.ScannedDictionaries, Is.EqualTo(1));
            Assert.That(property.Provenance.Resource.ScannedEntries, Is.LessThanOrEqualTo(1));
            Assert.That(property.Provenance.Resource.ScanComplete, Is.False);
            Assert.That(property.Provenance.Resource.Truncated, Is.True);
            Assert.That(property.Provenance.Resource.TruncatedReason,
                Is.EqualTo("maxProvenanceCandidates"));
            Assert.That(property.Provenance.Resource.ScanEvidence.Reason,
                Is.EqualTo("resource_scan_budget_exhausted"));
        });
    }

    [Test]
    public async Task Provenance_hard_caps_property_count_at_one_hundred()
    {
        var result = await _mcp.CallToolAsync<GetComputedPropertiesResponse>("get_computed_properties", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Provenance_Default" },
            ["includeDefault"] = true,
            ["includeProvenance"] = true,
            ["maxProperties"] = 500,
            ["maxProvenanceCandidates"] = 0
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Properties, Has.Count.EqualTo(100));
            Assert.That(result.Truncated, Is.True);
            Assert.That(result.TruncatedReason, Is.EqualTo("maxProvenanceProperties"));
            Assert.That(result.Properties.All(property => property.Provenance is not null), Is.True);
        });
    }

    [Test]
    public async Task Default_computed_properties_output_remains_without_provenance()
    {
        var result = await _mcp.CallToolAsync<GetComputedPropertiesResponse>("get_computed_properties", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Provenance_Local" },
            ["propertyNames"] = new[] { "Width" }
        });

        Assert.That(result.Properties.Single().Provenance, Is.Null);
    }

    [Test]
    public async Task Structured_provenance_is_independent_of_the_legacy_value_source_field()
    {
        var result = await _mcp.CallToolAsync<GetComputedPropertiesResponse>("get_computed_properties", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["locator"] = new Dictionary<string, object?> { ["automationId"] = "Provenance_Local" },
            ["propertyNames"] = new[] { "Width" },
            ["includeSources"] = false,
            ["includeProvenance"] = true
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Properties.Single().ValueSource, Is.Null);
            Assert.That(result.Properties.Single().Provenance, Is.Not.Null);
            Assert.That(result.Properties.Single().Provenance!.ValueSource.BaseValueSource,
                Is.EqualTo(DependencyPropertyBaseValueSource.Local));
        });
    }

    private async Task<ComputedPropertyInfo> GetPropertyAsync(
        string automationId,
        string propertyName,
        int maxProvenanceCandidates = 20)
    {
        var result = await _mcp.CallToolAsync<GetComputedPropertiesResponse>("get_computed_properties", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["locator"] = new Dictionary<string, object?> { ["automationId"] = automationId },
            ["propertyNames"] = new[] { propertyName },
            ["includeProvenance"] = true,
            ["maxProvenanceCandidates"] = maxProvenanceCandidates
        });

        Assert.That(result.MissingPropertyNames, Is.Null.Or.Empty);
        Assert.That(result.Properties, Has.Count.EqualTo(1));
        Assert.That(result.Properties[0].Provenance, Is.Not.Null);
        return result.Properties[0];
    }

    private static bool ShouldSkipForMissingAssets(InvalidOperationException exception)
    {
        var message = exception.Message;
        return message.Contains("Phase 2 agent payload directory not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Phase 2 Snoop payload directory not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Phase 2 agent assembly not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Snoop injector launcher not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Snoop generic injector not found", StringComparison.OrdinalIgnoreCase);
    }
}
