using System.Diagnostics;
using System.Text.Json;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class StateObservationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private McpTestContext _mcp = null!;
    private CancellationTokenSource _testCts = null!;
    private string _sessionId = "";
    private string _markerPath = "";
    private int _pid;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        using var setupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _mcp = await McpTestContext.StartAsync(
            McpServerPaths.FindMcpServerExecutable(),
            toolProfile: "diagnostics",
            cancellationToken: setupCts.Token);
    }

    [SetUp]
    public async Task SetUp()
    {
        _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _markerPath = Path.Combine(
            Path.GetTempPath(),
            $"wpf-tools-mcp-observation-{Guid.NewGuid():N}.log");

        var executablePath = TestAppPaths.FindObservationProbeTestAppExecutable();
        var launched = await _mcp.CallToolAsync<LaunchAppResponse>(
            "launch_app",
            new Dictionary<string, object?>
            {
                ["exePath"] = executablePath,
                ["workingDirectory"] = Path.GetDirectoryName(executablePath)!,
                ["args"] = new[] { "--marker-path", _markerPath },
                ["reuseExistingInstance"] = false
            },
            _testCts.Token);

        _sessionId = launched.SessionId;
        _pid = launched.Pid;
        await WaitForMarkerAsync("started", TimeSpan.FromSeconds(5), _testCts.Token);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (!string.IsNullOrWhiteSpace(_sessionId))
        {
            try
            {
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                _ = await _mcp.CallToolAsync<CloseAppResponse>(
                    "terminate_app",
                    new Dictionary<string, object?>
                    {
                        ["sessionId"] = _sessionId,
                        ["timeoutMs"] = 3000
                    },
                    cleanupCts.Token);
            }
            catch
            {
            }
        }

        await KillProcessBestEffortAsync(_pid);

        try
        {
            File.Delete(_markerPath);
        }
        catch
        {
        }

        _sessionId = "";
        _markerPath = "";
        _pid = 0;
        _testCts.Dispose();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_mcp is not null)
        {
            await _mcp.DisposeAsync();
        }
    }

    [Test]
    public async Task Initial_values_are_read_on_the_dispatcher_and_scalar_values_are_truncated()
    {
        var subscription = await SubscribeAsync(
            dependencyProperties: ["Text", "IsEnabled", "Width"],
            dataContextPaths:
            [
                "Phase",
                "Count",
                "Nested.Mode",
                "LargeValue",
                "SamePrefixValue",
                "DispatcherGuardedValue"
            ],
            includeVisualMetadata: true,
            maxValueLength: 16);

        try
        {
            var capture = await PollUntilAsync(
                subscription.SubscriptionId,
                result => result.Events.Count(EventIsInitial) >= 9,
                TimeSpan.FromSeconds(5));
            var initial = capture.Events
                .Where(EventIsInitial)
                .Select(DeserializeObservationEvent)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(subscription.MaxNodes, Is.EqualTo(5_000));
                Assert.That(initial, Has.Length.EqualTo(9));
                Assert.That(initial.Select(item => item.Sequence).Distinct().ToArray(), Has.Length.EqualTo(9));
                Assert.That(initial.All(item => item.Kind == ObserveStateEventKind.Initial), Is.True);
                Assert.That(initial.All(item => item.OldValue is null), Is.True);
                Assert.That(initial.All(item => item.ObservedAtUtc >= subscription.StartedAtUtc), Is.True);
                Assert.That(initial.All(item => item.ObservedAtUtc <= subscription.ExpiresAtUtc), Is.True);
                Assert.That(initial.All(item => item.ElapsedMs >= 0), Is.True);
            });

            Assert.That(GetString(Find(initial, ObserveStateSource.DependencyProperty, "Text").NewValue), Is.EqualTo("idle"));
            Assert.That(GetBoolean(Find(initial, ObserveStateSource.DependencyProperty, "IsEnabled").NewValue), Is.True);
            Assert.That(GetDouble(Find(initial, ObserveStateSource.DependencyProperty, "Width").NewValue), Is.EqualTo(280));
            Assert.That(
                Find(initial, ObserveStateSource.DependencyProperty, "Text").Visual,
                Is.Not.Null);
            Assert.That(GetString(Find(initial, ObserveStateSource.DataContextPath, "Phase").NewValue), Is.EqualTo("idle"));
            Assert.That(GetInteger(Find(initial, ObserveStateSource.DataContextPath, "Count").NewValue), Is.Zero);
            Assert.That(GetString(Find(initial, ObserveStateSource.DataContextPath, "Nested.Mode").NewValue), Is.EqualTo("cold"));

            var large = Find(initial, ObserveStateSource.DataContextPath, "LargeValue").NewValue;
            Assert.Multiple(() =>
            {
                Assert.That(large.State, Is.EqualTo(ObserveStateValueState.Value));
                Assert.That(large.Truncated, Is.True);
                Assert.That(GetString(large), Is.EqualTo(new string('I', 13) + "..."));
            });

            var dispatcherGuarded = Find(
                initial,
                ObserveStateSource.DataContextPath,
                "DispatcherGuardedValue").NewValue;
            Assert.Multiple(() =>
            {
                Assert.That(dispatcherGuarded.State, Is.EqualTo(ObserveStateValueState.Value));
                Assert.That(dispatcherGuarded.Error, Is.Null);
                Assert.That(dispatcherGuarded.Truncated, Is.False);
                Assert.That(GetString(dispatcherGuarded), Is.EqualTo("dispatcher-only"));
            });

            await InvokeAsync("Observation_SetSamePrefix");
            await WaitForMarkerAsync("same-prefix-complete", TimeSpan.FromSeconds(5), _testCts.Token);
            var samePrefixCapture = await PollUntilAsync(
                subscription.SubscriptionId,
                result => result.Events
                    .Where(EventIsChange)
                    .Select(DeserializeObservationEvent)
                    .Any(item => string.Equals(item.Path, "SamePrefixValue", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5));
            var samePrefixChange = samePrefixCapture.Events
                .Where(EventIsChange)
                .Select(DeserializeObservationEvent)
                .Single(item => string.Equals(item.Path, "SamePrefixValue", StringComparison.Ordinal));
            Assert.Multiple(() =>
            {
                Assert.That(samePrefixChange.OldValue!.Truncated, Is.True);
                Assert.That(samePrefixChange.NewValue.Truncated, Is.True);
                Assert.That(GetString(samePrefixChange.OldValue), Is.EqualTo(GetString(samePrefixChange.NewValue)));
            });

            var payloadBounded = await SubscribeAsync(
                dataContextPaths: ["LargeValue"],
                maxQueue: 8,
                maxValueLength: 4_096,
                maxPayloadChars: 4_096);
            try
            {
                var boundedInitial = await PollUntilAsync(
                    payloadBounded.SubscriptionId,
                    result => result.Events.Any(EventIsInitial),
                    TimeSpan.FromSeconds(5));
                var compactInitial = DeserializeObservationEvent(
                    boundedInitial.Events.Single(EventIsInitial));
                Assert.Multiple(() =>
                {
                    Assert.That(compactInitial.Source, Is.EqualTo(ObserveStateSource.DataContextPath));
                    Assert.That(compactInitial.Path, Is.EqualTo("LargeValue"));
                    Assert.That(compactInitial.NewValue.Truncated, Is.True);
                    Assert.That(compactInitial.NewValue.Value, Is.Null);
                    Assert.That(compactInitial.Visual, Is.Null);
                    Assert.That(boundedInitial.Truncated, Is.GreaterThanOrEqualTo(1));
                });

                await InvokeAsync("Observation_SetLarge");
                await WaitForMarkerAsync("large-complete", TimeSpan.FromSeconds(5), _testCts.Token);
                var boundedChangeCapture = await PollUntilAsync(
                    payloadBounded.SubscriptionId,
                    result => result.Events.Any(EventIsChange),
                    TimeSpan.FromSeconds(5));
                var boundedChange = DeserializeObservationEvent(
                    boundedChangeCapture.Events.Single(EventIsChange));
                Assert.Multiple(() =>
                {
                    Assert.That(boundedChange.NewValue.Truncated, Is.True);
                    Assert.That(boundedChange.NewValue.Value, Is.Null);
                    Assert.That(boundedChange.Visual, Is.Null);
                    Assert.That(boundedChangeCapture.Truncated, Is.GreaterThanOrEqualTo(1));
                });
            }
            finally
            {
                await UnsubscribeBestEffortAsync(payloadBounded.SubscriptionId);
            }
        }
        finally
        {
            await UnsubscribeBestEffortAsync(subscription.SubscriptionId);
        }
    }

    [Test]
    public async Task Event_driven_observation_preserves_short_transitions_between_delivery_ticks()
    {
        var subscription = await SubscribeAsync(
            dependencyProperties: ["Text"],
            dataContextPaths: ["Phase", "Count", "Nested.Mode"],
            cadenceMs: 250,
            maxQueue: 64);

        try
        {
            _ = await PollUntilAsync(
                subscription.SubscriptionId,
                result => result.Events.Count(EventIsInitial) >= 4,
                TimeSpan.FromSeconds(5));

            await InvokeAsync("Observation_RunOrdered");
            await WaitForMarkerAsync("ordered-complete", TimeSpan.FromSeconds(5), _testCts.Token);

            var capture = await PollUntilAsync(
                subscription.SubscriptionId,
                result => result.Events.Count(EventIsChange) >= 12,
                TimeSpan.FromSeconds(5));
            var changes = capture.Events
                .Where(EventIsChange)
                .Select(DeserializeObservationEvent)
                .OrderBy(item => item.Sequence)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(subscription.CadenceMs, Is.EqualTo(250));
                Assert.That(changes, Has.Length.EqualTo(12));
                Assert.That(capture.Dropped, Is.Zero);
                Assert.That(capture.Coalesced, Is.Zero);
                Assert.That(capture.Truncated, Is.Zero);
            });

            AssertStringChain(
                changes,
                "Phase",
                ["idle", "queued", "degraded"],
                ["queued", "degraded", "ready"]);
            AssertIntegerChain(
                changes,
                "Count",
                [0, 1, 2],
                [1, 2, 3]);
            AssertStringChain(
                changes,
                "Nested.Mode",
                ["cold", "warming", "retrying"],
                ["warming", "retrying", "stable"]);
            AssertStringChain(
                changes,
                "Text",
                ["idle", "queued", "degraded"],
                ["queued", "degraded", "ready"]);

            for (var index = 1; index < changes.Length; index++)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(changes[index].Sequence, Is.GreaterThan(changes[index - 1].Sequence));
                    Assert.That(changes[index].ObservedAtUtc, Is.GreaterThanOrEqualTo(changes[index - 1].ObservedAtUtc));
                    Assert.That(changes[index].ElapsedMs, Is.GreaterThanOrEqualTo(changes[index - 1].ElapsedMs));
                });
            }

            Assert.Multiple(() =>
            {
                Assert.That(changes.All(item => item.Kind == ObserveStateEventKind.Change), Is.True);
                Assert.That(changes.All(item => item.PreviousValueDurationMs >= 0), Is.True);
                Assert.That(changes[^1].ElapsedMs - changes[0].ElapsedMs, Is.GreaterThanOrEqualTo(40));
                Assert.That(changes[^1].ElapsedMs - changes[0].ElapsedMs, Is.LessThan(1500));
            });
        }
        finally
        {
            await UnsubscribeBestEffortAsync(subscription.SubscriptionId);
        }
    }

    [Test]
    public async Task Full_queue_coalesces_same_watch_and_reports_the_loss_explicitly()
    {
        var subscription = await SubscribeAsync(
            dataContextPaths: ["Phase"],
            cadenceMs: 250,
            maxQueue: 2);

        try
        {
            _ = await PollUntilAsync(
                subscription.SubscriptionId,
                result => result.Events.Count(EventIsInitial) >= 1,
                TimeSpan.FromSeconds(5));

            await InvokeAsync("Observation_RunCoalesced");
            await WaitForMarkerAsync("coalesced-complete", TimeSpan.FromSeconds(5), _testCts.Token);

            var capture = await PollUntilAsync(
                subscription.SubscriptionId,
                result => result.Coalesced >= 2 && result.Events.Count(EventIsChange) >= 2,
                TimeSpan.FromSeconds(5));
            var changes = capture.Events
                .Where(EventIsChange)
                .Select(DeserializeObservationEvent)
                .OrderBy(item => item.Sequence)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(changes, Has.Length.EqualTo(2));
                Assert.That(capture.Coalesced, Is.EqualTo(2));
                Assert.That(capture.Last.CoalescedTotal, Is.EqualTo(2));
                Assert.That(capture.Dropped, Is.Zero);
                Assert.That(GetString(changes[0].OldValue!), Is.EqualTo("idle"));
                Assert.That(GetString(changes[0].NewValue), Is.EqualTo("c1"));
                Assert.That(GetString(changes[1].OldValue!), Is.EqualTo("c1"));
                Assert.That(GetString(changes[1].NewValue), Is.EqualTo("c4"));
                Assert.That(changes[1].CoalescedChangeCount, Is.EqualTo(2));
                Assert.That(changes[1].Sequence - changes[0].Sequence, Is.EqualTo(3));
            });
        }
        finally
        {
            await UnsubscribeBestEffortAsync(subscription.SubscriptionId);
        }
    }

    [Test]
    public async Task Full_queue_drops_oldest_distinct_watches_and_reports_the_loss_explicitly()
    {
        var subscription = await SubscribeAsync(
            dataContextPaths: ["Phase", "Count", "Nested.Mode"],
            cadenceMs: 250,
            maxQueue: 2);

        try
        {
            var initial = await PollUntilAsync(
                subscription.SubscriptionId,
                result => result.Events.Count(EventIsInitial) >= 2,
                TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(initial.Events.Count(EventIsInitial), Is.EqualTo(2));
                Assert.That(initial.Dropped, Is.EqualTo(1));
            });

            await InvokeAsync("Observation_RunDropBurst");
            await WaitForMarkerAsync("drop-complete", TimeSpan.FromSeconds(5), _testCts.Token);

            var capture = await PollUntilAsync(
                subscription.SubscriptionId,
                result => result.Dropped >= 16 && result.Events.Count(EventIsChange) >= 2,
                TimeSpan.FromSeconds(5));
            var changes = capture.Events
                .Where(EventIsChange)
                .Select(DeserializeObservationEvent)
                .OrderBy(item => item.Sequence)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(changes, Has.Length.EqualTo(2));
                Assert.That(capture.Dropped, Is.EqualTo(16));
                Assert.That(capture.Last.DroppedTotal, Is.EqualTo(17));
                Assert.That(capture.Coalesced, Is.Zero);
                Assert.That(changes.Select(item => item.Path), Is.EqualTo(new[] { "Count", "Nested.Mode" }));
                Assert.That(GetInteger(changes[0].NewValue), Is.EqualTo(6));
                Assert.That(GetString(changes[1].NewValue), Is.EqualTo("drop-mode-6"));
            });
        }
        finally
        {
            await UnsubscribeBestEffortAsync(subscription.SubscriptionId);
        }
    }

    [Test]
    public async Task Duration_completion_remains_pollable_until_unsubscribe()
    {
        var subscription = await SubscribeAsync(
            dataContextPaths: ["Phase"],
            cadenceMs: 50,
            durationMs: 250);

        _ = await PollUntilAsync(
            subscription.SubscriptionId,
            result => result.Events.Count(EventIsInitial) >= 1,
            TimeSpan.FromSeconds(5));

        var completed = await PollUntilAsync(
            subscription.SubscriptionId,
            result => result.Last.Completed,
            TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(completed.Last.Completed, Is.True);
            Assert.That(completed.Last.CompletionReason, Is.EqualTo("duration_elapsed"));
            Assert.That(completed.Last.CompletedAtUtc, Is.Not.Null.And.Not.Empty);
            Assert.That(DateTimeOffset.TryParse(completed.Last.CompletedAtUtc, out _), Is.True);
        });

        var stillPollable = await PollOnceAsync(subscription.SubscriptionId, timeoutMs: 0);
        Assert.Multiple(() =>
        {
            Assert.That(stillPollable.Completed, Is.True);
            Assert.That(stillPollable.CompletionReason, Is.EqualTo("duration_elapsed"));
            Assert.That(stillPollable.CompletedAtUtc, Is.EqualTo(completed.Last.CompletedAtUtc));
        });

        var unsubscribed = await _mcp.CallToolAsync<UnsubscribeResponse>(
            "unsubscribe",
            new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["subscriptionId"] = subscription.SubscriptionId
            },
            _testCts.Token);
        Assert.That(unsubscribed.Unsubscribed, Is.True);
    }

    [Test]
    public async Task Element_unload_completes_and_detaches_the_observation()
    {
        var subscription = await SubscribeAsync(
            dependencyProperties: ["Text"],
            dataContextPaths: ["Phase"],
            durationMs: 30_000);

        _ = await PollUntilAsync(
            subscription.SubscriptionId,
            result => result.Events.Count(EventIsInitial) >= 2,
            TimeSpan.FromSeconds(5));

        await InvokeAsync("Observation_RemoveTarget");
        await WaitForMarkerAsync("target-removed", TimeSpan.FromSeconds(5), _testCts.Token);

        var completed = await PollUntilAsync(
            subscription.SubscriptionId,
            result => result.Last.Completed,
            TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(completed.Last.Completed, Is.True);
            Assert.That(completed.Last.CompletionReason, Is.EqualTo("element_unloaded"));
            Assert.That(completed.Last.CompletedAtUtc, Is.Not.Null.And.Not.Empty);
        });

        var unsubscribed = await _mcp.CallToolAsync<UnsubscribeResponse>(
            "unsubscribe",
            new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["subscriptionId"] = subscription.SubscriptionId
            },
            _testCts.Token);
        Assert.That(unsubscribed.Unsubscribed, Is.True);
    }

    [Test]
    public async Task Secondary_dispatcher_window_is_observed_and_released_on_its_owner()
    {
        await InvokeAsync("Observation_OpenSecondary");
        await WaitForMarkerAsync("secondary-started", TimeSpan.FromSeconds(5), _testCts.Token);
        var secondaryWindow = await WaitForWindowAsync(
            "WPF Tools MCP ObservationProbe Secondary",
            TimeSpan.FromSeconds(5));

        var subscription = await SubscribeAsync(
            dependencyProperties: ["Text"],
            dataContextPaths: ["Phase", "DispatcherGuardedValue"],
            durationMs: 30_000,
            windowHandle: secondaryWindow.Handle,
            automationId: "Observation_SecondaryTarget");
        var unsubscribed = false;
        try
        {
            var capture = await PollUntilAsync(
                subscription.SubscriptionId,
                result => result.Events.Count(EventIsInitial) >= 3,
                TimeSpan.FromSeconds(5));
            var initial = capture.Events
                .Where(EventIsInitial)
                .Select(DeserializeObservationEvent)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(GetString(Find(initial, ObserveStateSource.DependencyProperty, "Text").NewValue), Is.EqualTo("idle"));
                Assert.That(GetString(Find(initial, ObserveStateSource.DataContextPath, "Phase").NewValue), Is.EqualTo("idle"));
                Assert.That(
                    GetString(Find(initial, ObserveStateSource.DataContextPath, "DispatcherGuardedValue").NewValue),
                    Is.EqualTo("dispatcher-only"));
            });

            await InvokeAsync("Observation_SecondaryChange", secondaryWindow.Handle);
            await WaitForMarkerAsync(
                "secondary-change-complete",
                TimeSpan.FromSeconds(5),
                _testCts.Token);
            var changes = await PollUntilAsync(
                subscription.SubscriptionId,
                result => result.Events.Any(EventIsChange),
                TimeSpan.FromSeconds(5));
            Assert.That(
                changes.Events
                    .Where(EventIsChange)
                    .Select(DeserializeObservationEvent)
                    .Any(item =>
                        item.Source == ObserveStateSource.DataContextPath &&
                        string.Equals(item.Path, "Phase", StringComparison.Ordinal) &&
                        string.Equals(GetString(item.NewValue), "secondary-changed", StringComparison.Ordinal)),
                Is.True);

            var unsubscribe = await _mcp.CallToolAsync<UnsubscribeResponse>(
                "unsubscribe",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["subscriptionId"] = subscription.SubscriptionId
                },
                _testCts.Token);
            Assert.That(unsubscribe.Unsubscribed, Is.True);
            unsubscribed = true;
        }
        finally
        {
            if (!unsubscribed)
            {
                await UnsubscribeBestEffortAsync(subscription.SubscriptionId);
            }
        }
    }

    [Test]
    public async Task Detach_releases_the_owned_subscription_without_stopping_the_process()
    {
        var firstSessionId = _sessionId;
        var first = await SubscribeAsync(dataContextPaths: ["Phase"], durationMs: 30_000);
        _ = await PollUntilAsync(
            first.SubscriptionId,
            result => result.Events.Count(EventIsInitial) >= 1,
            TimeSpan.FromSeconds(5));

        var attached = await _mcp.CallToolAsync<AttachToAppResponse>(
            "attach_to_app",
            new Dictionary<string, object?> { ["pid"] = _pid },
            _testCts.Token);
        _sessionId = attached.SessionId;

        var detached = await _mcp.CallToolAsync<DetachSessionResponse>(
            "detach_session",
            new Dictionary<string, object?> { ["sessionId"] = firstSessionId },
            _testCts.Token);
        Assert.Multiple(() =>
        {
            Assert.That(detached.SessionRemoved, Is.True);
            Assert.That(detached.ProcessStillRunningObserved, Is.True);
            Assert.That(detached.ProcessStillRunning, Is.True);
            Assert.That(IsProcessRunning(_pid), Is.True);
        });

        var oldSubscriptionFailure = await CaptureToolFailureAsync(() =>
            _mcp.CallToolAsync<PollSubscriptionResponse>(
                "poll_subscription",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = attached.SessionId,
                    ["subscriptionId"] = first.SubscriptionId,
                    ["timeoutMs"] = 0
                },
                _testCts.Token));
        Assert.That(oldSubscriptionFailure.Message, Does.Contain("Unknown subscriptionId").IgnoreCase);

        var replacement = await SubscribeAsync(dataContextPaths: ["Phase"], durationMs: 5_000);
        try
        {
            var replacementInitial = await PollUntilAsync(
                replacement.SubscriptionId,
                result => result.Events.Count(EventIsInitial) >= 1,
                TimeSpan.FromSeconds(5));
            Assert.That(
                GetString(DeserializeObservationEvent(replacementInitial.Events.Single(EventIsInitial)).NewValue),
                Is.EqualTo("idle"));
        }
        finally
        {
            await UnsubscribeBestEffortAsync(replacement.SubscriptionId);
        }
    }

    private async Task<SubscribePropertyChangesResponse> SubscribeAsync(
        string[]? dependencyProperties = null,
        string[]? dataContextPaths = null,
        int cadenceMs = 50,
        int durationMs = 5_000,
        int maxQueue = 64,
        int maxValueLength = 512,
        int maxPayloadChars = 262_144,
        bool includeVisualMetadata = false,
        long? windowHandle = null,
        string automationId = "Observation_Target")
    {
        try
        {
            return await _mcp.CallToolAsync<SubscribePropertyChangesResponse>(
                "subscribe_property_changes",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["locator"] = new Dictionary<string, object?>
                    {
                        ["automationId"] = automationId
                    },
                    ["windowHandle"] = windowHandle,
                    ["dependencyProperties"] = dependencyProperties,
                    ["dataContextPaths"] = dataContextPaths,
                    ["cadenceMs"] = cadenceMs,
                    ["durationMs"] = durationMs,
                    ["maxQueue"] = maxQueue,
                    ["maxValueLength"] = maxValueLength,
                    ["maxPayloadChars"] = maxPayloadChars,
                    ["includeVisualMetadata"] = includeVisualMetadata
                },
                _testCts.Token);
        }
        catch (InvalidOperationException ex) when (ShouldSkipForMissingAssets(ex))
        {
            Assert.Ignore(ex.Message);
            throw;
        }
    }

    private async Task<WindowInfo> WaitForWindowAsync(string title, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var response = await _mcp.CallToolAsync<ListWindowsResponse>(
                "list_windows",
                new Dictionary<string, object?> { ["sessionId"] = _sessionId },
                _testCts.Token);
            var window = response.Windows.FirstOrDefault(item =>
                string.Equals(item.Title, title, StringComparison.Ordinal));
            if (window is not null)
            {
                return window;
            }

            await Task.Delay(100, _testCts.Token);
        }

        throw new TimeoutException($"Window '{title}' did not appear within {timeout}.");
    }

    private async Task InvokeAsync(string automationId, long? windowHandle = null)
    {
        var response = await _mcp.CallToolAsync<InvokeResponse>(
            "invoke",
            new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = automationId
                },
                ["windowHandle"] = windowHandle
            },
            _testCts.Token);
        Assert.That(response.Invoked, Is.True, $"Expected {automationId} to be invoked.");
    }

    private async Task<PollCapture> PollUntilAsync(
        string subscriptionId,
        Func<PollCapture, bool> condition,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        var events = new List<SubscriptionEvent>();
        var dropped = 0;
        var coalesced = 0;
        var truncated = 0;

        while (stopwatch.Elapsed < timeout)
        {
            var response = await PollOnceAsync(subscriptionId, timeoutMs: 500);
            events.AddRange(response.Events);
            dropped += response.Dropped;
            coalesced += response.Coalesced;
            truncated += response.Truncated;

            var capture = new PollCapture(events.ToArray(), dropped, coalesced, truncated, response);
            if (condition(capture))
            {
                return capture;
            }
        }

        throw new AssertionException(
            $"Subscription '{subscriptionId}' did not reach the expected state within {timeout.TotalSeconds:0.###} seconds.");
    }

    private Task<PollSubscriptionResponse> PollOnceAsync(string subscriptionId, int timeoutMs) =>
        _mcp.CallToolAsync<PollSubscriptionResponse>(
            "poll_subscription",
            new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["subscriptionId"] = subscriptionId,
                ["maxBatch"] = 500,
                ["timeoutMs"] = timeoutMs
            },
            _testCts.Token);

    private async Task UnsubscribeBestEffortAsync(string subscriptionId)
    {
        try
        {
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            _ = await _mcp.CallToolAsync<UnsubscribeResponse>(
                "unsubscribe",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["subscriptionId"] = subscriptionId
                },
                cleanupCts.Token);
        }
        catch
        {
        }
    }

    private async Task WaitForMarkerAsync(
        string marker,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(_markerPath) && File.ReadAllLines(_markerPath).Contains(marker, StringComparer.Ordinal))
                {
                    return;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new AssertionException($"Marker '{marker}' was not written within {timeout.TotalSeconds:0.###} seconds.");
    }

    private static ObserveStateEvent DeserializeObservationEvent(SubscriptionEvent subscriptionEvent) =>
        subscriptionEvent.Payload.Deserialize<ObserveStateEvent>(JsonOptions)
        ?? throw new AssertionException($"Invalid observation payload for '{subscriptionEvent.Kind}'.");

    private static bool EventIsInitial(SubscriptionEvent item) =>
        string.Equals(item.Kind, "property_initial", StringComparison.Ordinal);

    private static bool EventIsChange(SubscriptionEvent item) =>
        string.Equals(item.Kind, "property_changed", StringComparison.Ordinal);

    private static ObserveStateEvent Find(
        IEnumerable<ObserveStateEvent> events,
        ObserveStateSource source,
        string path) =>
        events.Single(item => item.Source == source && string.Equals(item.Path, path, StringComparison.Ordinal));

    private static void AssertStringChain(
        IEnumerable<ObserveStateEvent> events,
        string path,
        IReadOnlyList<string> expectedOld,
        IReadOnlyList<string> expectedNew)
    {
        var chain = events.Where(item => string.Equals(item.Path, path, StringComparison.Ordinal)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(chain.Select(item => GetString(item.OldValue!)), Is.EqualTo(expectedOld));
            Assert.That(chain.Select(item => GetString(item.NewValue)), Is.EqualTo(expectedNew));
        });
    }

    private static void AssertIntegerChain(
        IEnumerable<ObserveStateEvent> events,
        string path,
        IReadOnlyList<int> expectedOld,
        IReadOnlyList<int> expectedNew)
    {
        var chain = events.Where(item => string.Equals(item.Path, path, StringComparison.Ordinal)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(chain.Select(item => GetInteger(item.OldValue!)), Is.EqualTo(expectedOld));
            Assert.That(chain.Select(item => GetInteger(item.NewValue)), Is.EqualTo(expectedNew));
        });
    }

    private static string GetString(ObserveStateValue value)
    {
        Assert.That(value.State, Is.EqualTo(ObserveStateValueState.Value));
        return value.Value?.GetValue<string>()
               ?? throw new AssertionException("Expected a string observation value.");
    }

    private static bool GetBoolean(ObserveStateValue value)
    {
        Assert.That(value.State, Is.EqualTo(ObserveStateValueState.Value));
        return value.Value?.GetValue<bool>()
               ?? throw new AssertionException("Expected a Boolean observation value.");
    }

    private static int GetInteger(ObserveStateValue value)
    {
        Assert.That(value.State, Is.EqualTo(ObserveStateValueState.Value));
        return value.Value?.GetValue<int>()
               ?? throw new AssertionException("Expected an integer observation value.");
    }

    private static double GetDouble(ObserveStateValue value)
    {
        Assert.That(value.State, Is.EqualTo(ObserveStateValueState.Value));
        return value.Value?.GetValue<double>()
               ?? throw new AssertionException("Expected a floating-point observation value.");
    }

    private static async Task<InvalidOperationException> CaptureToolFailureAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }

        throw new AssertionException("Expected the MCP tool call to fail.");
    }

    private static bool ShouldSkipForMissingAssets(InvalidOperationException ex)
    {
        var message = ex.Message;
        return message.Contains("Phase 2 agent payload directory not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Phase 2 Snoop payload directory not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Phase 2 agent assembly not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Snoop injector launcher not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Snoop generic injector not found", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task KillProcessBestEffortAsync(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(exitCts.Token);
        }
        catch
        {
        }
    }

    private sealed record PollCapture(
        IReadOnlyList<SubscriptionEvent> Events,
        int Dropped,
        int Coalesced,
        int Truncated,
        PollSubscriptionResponse Last);
}
