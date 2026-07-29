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
}
