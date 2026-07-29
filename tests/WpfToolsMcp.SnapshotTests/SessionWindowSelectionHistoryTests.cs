using WpfToolsMcp.Automation;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class SessionWindowSelectionHistoryTests
{
    [Test]
    public void Reconcile_restores_the_most_recent_live_selection_before_main()
    {
        var history = new SessionWindowSelectionHistory();
        history.RecordSelection(101, "Main", preserveAsFallback: true);
        history.RecordSelection(202, "First dialog");
        history.RecordSelection(303, "Latest dialog");

        var first = history.Reconcile(handle => handle is 101 or 202);
        var second = history.Reconcile(handle => handle is 101 or 303);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(new SessionWindowSelection(202, "First dialog")));
            Assert.That(second, Is.EqualTo(new SessionWindowSelection(101, "Main")));
            Assert.That(history.GetActive(), Is.EqualTo(second));
        });
    }

    [Test]
    public void RecordSelection_keeps_a_bounded_deduplicated_history_with_main_as_fallback()
    {
        var history = new SessionWindowSelectionHistory(capacity: 3);
        history.RecordSelection(1, "Main", preserveAsFallback: true);
        history.RecordSelection(2, "Dialog 2");
        history.RecordSelection(3, "Dialog 3");
        history.RecordSelection(4, "Dialog 4");
        history.RecordSelection(3, "Dialog 3 updated");
        var inspectedHandles = new List<long>();

        var active = history.Reconcile(handle =>
        {
            inspectedHandles.Add(handle);
            return handle == 3;
        });

        Assert.Multiple(() =>
        {
            Assert.That(inspectedHandles, Is.EqualTo(new long[] { 3, 4, 1 }));
            Assert.That(active, Is.EqualTo(new SessionWindowSelection(3, "Dialog 3 updated")));
        });
    }

    [Test]
    public async Task Reconcile_does_not_hold_its_lock_during_validation_or_overwrite_a_new_selection()
    {
        var history = new SessionWindowSelectionHistory();
        history.RecordSelection(10, "Main", preserveAsFallback: true);
        history.RecordSelection(20, "Dialog");
        var validationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseValidation = new ManualResetEventSlim();
        var validationBlocked = 0;

        var reconciliation = Task.Run(() => history.Reconcile(handle =>
        {
            if (handle == 20 && Interlocked.Exchange(ref validationBlocked, 1) == 0)
            {
                validationEntered.SetResult();
                _ = releaseValidation.Wait(TimeSpan.FromSeconds(5));
                return false;
            }

            return handle is 10 or 30;
        }));

        try
        {
            await validationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var recordSelection = Task.Run(() => history.RecordSelection(30, "New dialog"));
            await recordSelection.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseValidation.Set();
        }

        var active = await reconciliation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(active, Is.EqualTo(new SessionWindowSelection(30, "New dialog")));
    }

    [Test]
    public void Reconcile_selects_the_enabled_nested_modal_then_restores_each_owner()
    {
        var history = new SessionWindowSelectionHistory();
        history.RecordSelection(100, "Main", preserveAsFallback: true);

        var firstDialog = Reconcile(history, Graph(
            Window(100, "Main", isEnabled: false, zOrder: 2),
            Window(200, "First dialog", ownerHandle: 100, zOrder: 0)));

        var nestedDialog = Reconcile(history, Graph(
            Window(100, "Main", isEnabled: false, zOrder: 3),
            Window(200, "First dialog", ownerHandle: 100, isEnabled: false, zOrder: 1),
            Window(300, "Nested dialog", ownerHandle: 200, zOrder: 0)));

        var restoredFirstDialog = Reconcile(history, Graph(
            Window(100, "Main", isEnabled: false, zOrder: 2),
            Window(200, "First dialog", ownerHandle: 100, zOrder: 0)));

        var restoredMain = Reconcile(history, Graph(
            Window(100, "Main", zOrder: 0)));

        Assert.Multiple(() =>
        {
            Assert.That(firstDialog, Is.EqualTo(new SessionWindowSelection(200, "First dialog")));
            Assert.That(nestedDialog, Is.EqualTo(new SessionWindowSelection(300, "Nested dialog")));
            Assert.That(restoredFirstDialog, Is.EqualTo(new SessionWindowSelection(200, "First dialog")));
            Assert.That(restoredMain, Is.EqualTo(new SessionWindowSelection(100, "Main")));
        });
    }

    [Test]
    public void Reconcile_discovers_a_modal_on_another_root_and_restores_its_owner()
    {
        var history = new SessionWindowSelectionHistory();
        history.RecordSelection(100, "Current root", preserveAsFallback: true);

        var modal = Reconcile(history, Graph(
            Window(100, "Current root", zOrder: 3),
            Window(400, "Other root", isEnabled: false, zOrder: 2),
            Window(500, "Other modal", ownerHandle: 400, zOrder: 0)));

        var restoredOtherRoot = Reconcile(history, Graph(
            Window(100, "Current root", zOrder: 2),
            Window(400, "Other root", zOrder: 0)));

        var restoredOriginalRoot = Reconcile(history, Graph(
            Window(100, "Current root", zOrder: 0)));

        Assert.Multiple(() =>
        {
            Assert.That(modal, Is.EqualTo(new SessionWindowSelection(500, "Other modal")));
            Assert.That(restoredOtherRoot, Is.EqualTo(new SessionWindowSelection(400, "Other root")));
            Assert.That(restoredOriginalRoot, Is.EqualTo(new SessionWindowSelection(100, "Current root")));
        });
    }

    [Test]
    public void Reconcile_invalidates_automatic_modals_that_are_hidden_or_no_longer_modal()
    {
        var hiddenHistory = CreateHistoryWithSelectedModal();
        var afterHidden = Reconcile(hiddenHistory, Graph(
            Window(100, "Main", isEnabled: false, zOrder: 1),
            Window(200, "Dialog", ownerHandle: 100, isVisible: false, zOrder: 0)));

        var reenabledHistory = CreateHistoryWithSelectedModal();
        var afterOwnerReenabled = Reconcile(reenabledHistory, Graph(
            Window(100, "Main", zOrder: 1),
            Window(200, "Dialog", ownerHandle: 100, zOrder: 0)));

        Assert.Multiple(() =>
        {
            Assert.That(afterHidden, Is.EqualTo(new SessionWindowSelection(100, "Main")));
            Assert.That(afterOwnerReenabled, Is.EqualTo(new SessionWindowSelection(100, "Main")));
        });
    }

    [Test]
    public void Reconcile_prefers_the_frontmost_modal_across_ownership_groups()
    {
        var graph = Graph(
            Window(100, "Active root", isEnabled: false, zOrder: 6),
            Window(200, "Related modal", ownerHandle: 100, zOrder: 5),
            Window(300, "Other root", isEnabled: false, zOrder: 2),
            Window(400, "Frontmost modal", ownerHandle: 300, zOrder: 0),
            Window(500, "Neutral root", zOrder: 4));

        var relatedHistory = new SessionWindowSelectionHistory();
        relatedHistory.RecordSelection(100, "Active root", preserveAsFallback: true);
        var related = Reconcile(relatedHistory, graph);

        var neutralHistory = new SessionWindowSelectionHistory();
        neutralHistory.RecordSelection(500, "Neutral root", preserveAsFallback: true);
        var frontmost = Reconcile(neutralHistory, graph);

        Assert.Multiple(() =>
        {
            Assert.That(related, Is.EqualTo(new SessionWindowSelection(400, "Frontmost modal")));
            Assert.That(frontmost, Is.EqualTo(new SessionWindowSelection(400, "Frontmost modal")));
        });
    }

    private static SessionWindowSelectionHistory CreateHistoryWithSelectedModal()
    {
        var history = new SessionWindowSelectionHistory();
        history.RecordSelection(100, "Main", preserveAsFallback: true);
        _ = Reconcile(history, Graph(
            Window(100, "Main", isEnabled: false, zOrder: 1),
            Window(200, "Dialog", ownerHandle: 100, zOrder: 0)));
        return history;
    }

    private static SessionWindowSelection Reconcile(
        SessionWindowSelectionHistory history,
        SessionTopLevelWindowGraph graph) =>
        SessionWindowReconciler.Reconcile(history, graph.ContainsWindow, graph);

    private static SessionTopLevelWindowGraph Graph(
        params SessionTopLevelWindowObservation[] windows) =>
        SessionTopLevelWindowGraph.Create(windows);

    private static SessionTopLevelWindowObservation Window(
        long handle,
        string title,
        long ownerHandle = 0,
        bool isVisible = true,
        bool isEnabled = true,
        int zOrder = 0) =>
        new(handle, title, ownerHandle, isVisible, isEnabled, zOrder);
}
