using System.Text.Json;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class UiaMappingDecisionTests
{
    [Test]
    public void Built_in_identity_is_exact_only_with_automation_id_type_and_runtime_identity()
    {
        var candidate = Score(
            source: new("Basic_Button", "Click me", "Button", new Rect(10, 20, 100, 30)),
            candidate: new("Basic_Button", "Click me", "Button", new Rect(10, 20, 100, 30)),
            typeCompatible: true,
            reusable: true);

        var decision = ElementMappingScoring.Decide([candidate], scanComplete: true);

        Assert.Multiple(() =>
        {
            Assert.That(candidate.Score, Is.EqualTo(330));
            Assert.That(decision.Status, Is.EqualTo(ElementMappingStatus.Exact));
            Assert.That(decision.SelectedIndex, Is.Zero);
            Assert.That(decision.Evidence, Does.Contain("unique_exact_automation_id_and_control_type"));
        });
    }

    [Test]
    public void Templated_peer_without_automation_id_can_be_heuristic_but_not_exact()
    {
        var candidate = Score(
            source: new("Templated_Button", "Templated button", "Button", new Rect(10, 20, 100, 30)),
            candidate: new(null, "Templated button", "Button", new Rect(10, 20, 100, 30)),
            typeCompatible: true,
            reusable: true);

        var decision = ElementMappingScoring.Decide([candidate], scanComplete: true);

        Assert.Multiple(() =>
        {
            Assert.That(candidate.Score, Is.EqualTo(230));
            Assert.That(candidate.Evidence, Does.Contain("automation_id_missing"));
            Assert.That(decision.Status, Is.EqualTo(ElementMappingStatus.Heuristic));
            Assert.That(decision.SelectedIndex, Is.Zero);
        });
    }

    [Test]
    public void Equal_bounds_and_identity_scores_are_ambiguous_without_a_selected_candidate()
    {
        var first = Candidate(score: 280, automationIdExact: true, typeCompatible: true);
        var second = Candidate(score: 280, automationIdExact: true, typeCompatible: true);

        var decision = ElementMappingScoring.Decide([first, second], scanComplete: true);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Status, Is.EqualTo(ElementMappingStatus.Ambiguous));
            Assert.That(decision.SelectedIndex, Is.Null);
            Assert.That(decision.ScoreLead, Is.Zero);
            Assert.That(decision.Evidence, Does.Contain("top_score_tied"));
        });
    }

    [Test]
    public void Incomplete_scan_never_selects_even_when_no_candidate_was_seen()
    {
        var decision = ElementMappingScoring.Decide([], scanComplete: false);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Status, Is.EqualTo(ElementMappingStatus.Ambiguous));
            Assert.That(decision.SelectedIndex, Is.Null);
            Assert.That(decision.Evidence, Is.EqualTo(new[] { "scan_incomplete" }));
        });
    }

    [Test]
    public void Complete_scan_without_relevant_candidates_is_unmapped()
    {
        var decision = ElementMappingScoring.Decide([], scanComplete: true);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Status, Is.EqualTo(ElementMappingStatus.Unmapped));
            Assert.That(decision.SelectedIndex, Is.Null);
            Assert.That(decision.Evidence, Does.Contain("no_relevant_candidates"));
        });
    }

    [Test]
    public void Bounds_only_candidate_is_reported_as_weak_ambiguity()
    {
        var candidate = Score(
            source: new(null, null, null, new Rect(10, 20, 100, 30)),
            candidate: new(null, null, null, new Rect(10, 20, 100, 30)),
            typeCompatible: false,
            reusable: true);

        var decision = ElementMappingScoring.Decide([candidate], scanComplete: true);

        Assert.Multiple(() =>
        {
            Assert.That(candidate.Score, Is.EqualTo(140));
            Assert.That(decision.Status, Is.EqualTo(ElementMappingStatus.Ambiguous));
            Assert.That(decision.SelectedIndex, Is.Null);
            Assert.That(decision.Evidence, Does.Contain("score_below_heuristic_threshold"));
        });
    }

    [TestCase(39, ElementMappingStatus.Ambiguous)]
    [TestCase(40, ElementMappingStatus.Heuristic)]
    public void Heuristic_selection_requires_a_forty_point_lead(int lead, ElementMappingStatus expected)
    {
        var top = Candidate(score: 200, automationIdExact: false, typeCompatible: true);
        var runnerUp = Candidate(score: 200 - lead, automationIdExact: false, typeCompatible: true);

        var decision = ElementMappingScoring.Decide([top, runnerUp], scanComplete: true);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Status, Is.EqualTo(expected));
            Assert.That(decision.ScoreLead, Is.EqualTo(lead));
            Assert.That(decision.SelectedIndex, Is.EqualTo(expected == ElementMappingStatus.Heuristic ? 0 : null));
        });
    }

    [Test]
    public void Candidate_without_runtime_identity_is_never_selected()
    {
        var decision = ElementMappingScoring.Decide(
            [Candidate(score: 330, automationIdExact: true, typeCompatible: true, reusable: false)],
            scanComplete: true);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Status, Is.EqualTo(ElementMappingStatus.Ambiguous));
            Assert.That(decision.SelectedIndex, Is.Null);
            Assert.That(decision.Evidence, Does.Contain("runtime_identity_unavailable"));
        });
    }

    [Test]
    public void Duplicate_exact_identity_without_reusable_runtime_id_prevents_exact_status()
    {
        var decision = ElementMappingScoring.Decide(
            [
                Candidate(score: 330, automationIdExact: true, typeCompatible: true),
                Candidate(score: 250, automationIdExact: true, typeCompatible: true, reusable: false)
            ],
            scanComplete: true);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Status, Is.EqualTo(ElementMappingStatus.Heuristic));
            Assert.That(decision.SelectedIndex, Is.Zero);
            Assert.That(decision.Evidence, Does.Contain("exact_identity_not_unique"));
        });
    }

    [Test]
    public void Unique_exact_identity_does_not_require_the_heuristic_score_lead()
    {
        var decision = ElementMappingScoring.Decide(
            [
                Candidate(score: 200, automationIdExact: true, typeCompatible: true),
                Candidate(score: 161, automationIdExact: false, typeCompatible: true)
            ],
            scanComplete: true);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Status, Is.EqualTo(ElementMappingStatus.Exact));
            Assert.That(decision.SelectedIndex, Is.Zero);
            Assert.That(decision.ScoreLead, Is.EqualTo(39));
            Assert.That(decision.Evidence, Does.Contain("unique_exact_automation_id_and_control_type"));
        });
    }

    [Test]
    public void Element_id_mapping_rejects_a_cross_window_scope()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            AutomationController.ValidateUiaMappingWindowScope(
                requestedWindowHandle: 202,
                elementWindowHandle: 101));

        Assert.That(error!.Message, Does.StartWith("windowHandle does not match the elementId window."));
    }

    [TestCase(42, 42, true)]
    [TestCase(41, 42, false)]
    [TestCase(null, 42, false)]
    public void Mapping_candidates_must_have_the_attached_process_identity(
        int? candidateProcessId,
        int attachedProcessId,
        bool expected)
    {
        Assert.That(
            AutomationController.IsUiaMappingProcessInScope(candidateProcessId, attachedProcessId),
            Is.EqualTo(expected));
    }

    [Test]
    public void Mapping_source_refresh_does_not_restore_cleared_public_metadata()
    {
        var historical = new AutomationController.ElementHandle(
            InspectionBackend.Wpf,
            WindowHandle: 42,
            XPath: "/Window/Button",
            WpfAgentElementId: "agent_old",
            UiaRuntimeId: null,
            Type: "Button",
            AutomationId: "OldId",
            Name: "Old name",
            ClassName: "OldClass",
            Bounds: new Rect(1, 2, 3, 4));
        var current = new ElementRef(
            Type: "",
            AutomationId: null,
            Name: null,
            XPath: "/Window/Current",
            ClassName: null,
            Bounds: null,
            ElementIdWpf: null);

        var refreshed = AutomationController.RefreshWpfMappingSource(historical, current);

        Assert.Multiple(() =>
        {
            Assert.That(refreshed.XPath, Is.EqualTo(current.XPath));
            Assert.That(refreshed.WpfAgentElementId, Is.EqualTo("agent_old"));
            Assert.That(refreshed.Type, Is.EqualTo(""));
            Assert.That(refreshed.AutomationId, Is.Null);
            Assert.That(refreshed.Name, Is.Null);
            Assert.That(refreshed.ClassName, Is.Null);
            Assert.That(refreshed.Bounds, Is.Null);
        });
    }

    [Test]
    public void Mapping_traversal_budget_is_shared_bounded_and_preserves_the_first_reason()
    {
        var budget = new AutomationController.UiaMappingTraversalBudget(maxNodes: 2);

        Assert.That(budget.TryVisitNode(CancellationToken.None), Is.True);
        Assert.That(budget.TryVisitNode(CancellationToken.None), Is.True);
        Assert.That(budget.TryVisitNode(CancellationToken.None), Is.False);
        budget.MarkIncomplete("laterFailure");

        Assert.Multiple(() =>
        {
            Assert.That(budget.VisitedNodes, Is.EqualTo(2));
            Assert.That(budget.IncompleteReason, Is.EqualTo("maxNodes"));
        });
    }

    [Test]
    public void Mapping_traversal_budget_observes_cancellation_before_consuming_a_node()
    {
        var budget = new AutomationController.UiaMappingTraversalBudget(maxNodes: 2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => budget.TryVisitNode(cancellation.Token));
        Assert.That(budget.VisitedNodes, Is.Zero);
    }

    [Test]
    public void Mapping_traversal_budget_does_not_call_the_provider_after_exhaustion()
    {
        var budget = new AutomationController.UiaMappingTraversalBudget(maxNodes: 1);
        var providerCalls = 0;

        var firstRead = budget.TryReadNode(
            () =>
            {
                providerCalls++;
                return new object();
            },
            "providerUnavailable",
            CancellationToken.None,
            out _,
            out var firstBudgetExhausted);
        var secondRead = budget.TryReadNode(
            () =>
            {
                providerCalls++;
                return new object();
            },
            "providerUnavailable",
            CancellationToken.None,
            out _,
            out var secondBudgetExhausted);

        Assert.Multiple(() =>
        {
            Assert.That(firstRead, Is.True);
            Assert.That(firstBudgetExhausted, Is.False);
            Assert.That(secondRead, Is.False);
            Assert.That(secondBudgetExhausted, Is.True);
            Assert.That(providerCalls, Is.EqualTo(1));
            Assert.That(budget.VisitedNodes, Is.EqualTo(1));
            Assert.That(budget.IncompleteReason, Is.EqualTo("maxNodes"));
        });
    }

    [TestCase("Button", "Button", true)]
    [TestCase("TextBlock", "Text", true)]
    [TestCase("System.Windows.Controls.TextBox", "Edit", true)]
    [TestCase("PasswordBox", "Edit", true)]
    [TestCase("CustomChart", "Pane", false)]
    public void Wpf_to_uia_type_compatibility_is_deterministic(
        string wpfType,
        string uiaType,
        bool expected)
    {
        Assert.That(
            AutomationController.AreWpfAndUiaTypesCompatible(wpfType, uiaType),
            Is.EqualTo(expected));
    }

    [TestCase(0)]
    [TestCase(50_001)]
    public void Mapping_scan_rejects_out_of_range_limits_before_attachment(int maxNodes)
    {
        using var controller = new AutomationController();

        var error = Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            _ = await controller.GetUiaLocatorsAsync(
                locator: new ElementLocator(AutomationId: "Button"),
                maxNodes: maxNodes));

        Assert.That(error!.ParamName, Is.EqualTo("maxNodes"));
    }

    [Test]
    public void Mapping_rejects_auto_backend_before_attachment()
    {
        using var controller = new AutomationController();

        var error = Assert.ThrowsAsync<ArgumentException>(async () =>
            _ = await controller.GetUiaLocatorsAsync(
                locator: new ElementLocator(AutomationId: "Button"),
                backend: InspectionBackend.Auto));

        Assert.That(error!.ParamName, Is.EqualTo("backend"));
    }

    [Test]
    public void Mapping_contract_uses_status_and_integer_score_without_confidence_fields()
    {
        var response = new GetUiaLocatorsResponse(
            Wpf: new WpfLocatorIdentity("Button", "Button", "Click me", "Button", "/Window/Button", "wpf_1")
            {
                Bounds = new Rect(1, 2, 3, 4)
            },
            Uia: null,
            LocatorSuggestions: null,
            FlaUi: null,
            UiaMapping: new UiaMappingDiagnostics(
                Ambiguous: false,
                SelectedXPath: null,
                Candidates: [],
                ReturnedCandidates: 0,
                TotalCandidates: 0)
            {
                Status = ElementMappingStatus.Unmapped,
                Method = "scoredWindowScan",
                Score = 0,
                Evidence = ["no_relevant_candidates"],
                ScannedNodes = 12,
                ScanComplete = true
            });

        var json = JsonSerializer.Serialize(response);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"Status\":\"Unmapped\""));
            Assert.That(json, Does.Contain("\"Score\":0"));
            Assert.That(json.ToLowerInvariant(), Does.Not.Contain("confidence"));
            Assert.That(json, Does.Not.Contain("\"Uia\""));
        });
    }

    private static ElementMappingScoring.CandidateScore Score(
        ElementMappingScoring.Facts source,
        ElementMappingScoring.Facts candidate,
        bool typeCompatible,
        bool reusable) =>
        ElementMappingScoring.Score(source, candidate, typeCompatible, reusable)
        ?? throw new AssertionException("Expected a relevant mapping candidate.");

    private static ElementMappingScoring.CandidateScore Candidate(
        int score,
        bool automationIdExact,
        bool typeCompatible,
        bool reusable = true) =>
        new(
            score,
            automationIdExact,
            typeCompatible,
            reusable,
            [reusable ? "runtime_identity_available" : "runtime_identity_unavailable"]);
}
