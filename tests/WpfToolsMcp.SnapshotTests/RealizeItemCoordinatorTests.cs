using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class RealizeItemCoordinatorTests
{
    private static readonly DateTimeOffset ClockOrigin =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void Request_defaults_are_practical_and_bounded()
    {
        var request = new RealizeItemRequest();

        Assert.Multiple(() =>
        {
            Assert.That(request.MaxProviderCalls, Is.EqualTo(100));
            Assert.That(request.AdvisoryElapsedLimitMs, Is.EqualTo(5_000));
            Assert.That(request.PollIntervalMs, Is.EqualTo(50));
            Assert.That(RealizeItemLimits.MaximumProviderCalls, Is.EqualTo(1_000));
            Assert.That(RealizeItemLimits.MaximumAdvisoryElapsedLimitMs, Is.EqualTo(60_000));
            Assert.That(RealizeItemLimits.MinimumPollIntervalMs, Is.EqualTo(10));
            Assert.That(RealizeItemLimits.MaximumPollIntervalMs, Is.EqualTo(1_000));
        });
    }

    [Test]
    public void Coordinator_requires_exactly_one_valid_selector_and_caller_bounds()
    {
        var provider = new FakeProvider();

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<ArgumentException>(() => ExecuteAsync(new RealizeItemRequest(), provider));
            Assert.ThrowsAsync<ArgumentException>(() => ExecuteAsync(
                new RealizeItemRequest(Index: 0, Name: "Item"),
                provider));
            Assert.ThrowsAsync<ArgumentException>(() => ExecuteAsync(
                new RealizeItemRequest(Name: string.Empty),
                provider));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ExecuteAsync(
                new RealizeItemRequest(Index: -1),
                provider));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ExecuteAsync(
                new RealizeItemRequest(Index: 0, MaxProviderCalls: 0),
                provider));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ExecuteAsync(
                new RealizeItemRequest(Index: 0, MaxProviderCalls: 1_001),
                provider));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ExecuteAsync(
                new RealizeItemRequest(Index: 0, AdvisoryElapsedLimitMs: 0),
                provider));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ExecuteAsync(
                new RealizeItemRequest(Index: 0, AdvisoryElapsedLimitMs: 60_001),
                provider));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ExecuteAsync(
                new RealizeItemRequest(Index: 0, PollIntervalMs: 9),
                provider));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ExecuteAsync(
                new RealizeItemRequest(Index: 0, PollIntervalMs: 1_001),
                provider));
        });
    }

    [Test]
    public async Task Index_selector_enumerates_provider_order_and_polls_after_realization()
    {
        var clock = new ManualTimeProvider(ClockOrigin);
        var items = new[]
        {
            new FakeItem("zero"),
            new FakeItem("one"),
            new FakeItem("two")
        };
        var provider = new FakeProvider
        {
            FindNextHandler = startAfter => startAfter is null
                ? items[0]
                : items[Array.IndexOf(items, startAfter) + 1],
            TargetStateHandler = _ => RealizeItemTargetState.Virtualized,
            PostconditionHandler = (_, poll) => ValueTask.FromResult(
                poll == 1
                    ? RealizeItemPostconditionResult.Pending()
                    : RealizeItemPostconditionResult.Verified(
                        Element("two", "uia:two"),
                        reusable: true))
        };

        var response = await ExecuteAsync(
            new RealizeItemRequest(Index: 2),
            provider,
            clock,
            (delay, _) =>
            {
                clock.Advance(delay);
                return Task.CompletedTask;
            });

        Assert.Multiple(() =>
        {
            Assert.That(provider.FindNextStarts, Is.EqualTo(new FakeItem?[] { null, items[0], items[1] }));
            Assert.That(provider.RealizedTargets, Is.EqualTo(new[] { items[2] }));
            Assert.That(provider.PostconditionTargets, Is.EqualTo(new[] { items[2], items[2] }));
            Assert.That(response.RequestedIdentity, Is.EqualTo(new RealizeItemRequestedIdentity(Index: 2)));
            Assert.That(response.MethodUsed, Is.EqualTo(RealizeItemOutcomes.MethodVirtualizedItemRealize));
            Assert.That(response.RealizeInvoked, Is.True);
            Assert.That(response.PostconditionVerified, Is.True);
            Assert.That(response.FindItemByPropertyCalls, Is.EqualTo(3));
            Assert.That(response.PostconditionPolls, Is.EqualTo(2));
            Assert.That(response.ElapsedMs, Is.EqualTo(50));
            Assert.That(response.StopReason, Is.EqualTo(RealizeItemOutcomes.StopCompleted));
            Assert.That(response.ViewportMayHaveChanged, Is.True);
            Assert.That(response.DataOrContainerLoadingMayHaveOccurred, Is.True);
            Assert.That(response.Reusable, Is.True);
            Assert.That(response.Element?.ElementId, Is.EqualTo("uia:two"));
        });
    }

    [Test]
    public async Task Already_realized_target_is_verified_without_invoking_realize()
    {
        var item = new FakeItem("visible");
        var provider = new FakeProvider
        {
            FindNextHandler = _ => item,
            TargetStateHandler = _ => RealizeItemTargetState.AlreadyRealized,
            PostconditionHandler = (_, _) => ValueTask.FromResult(
                RealizeItemPostconditionResult.Verified(
                    Element("visible", "uia:visible"),
                    reusable: true))
        };

        var response = await ExecuteAsync(new RealizeItemRequest(Index: 0), provider);

        Assert.Multiple(() =>
        {
            Assert.That(response.MethodUsed, Is.EqualTo(RealizeItemOutcomes.MethodAlreadyRealized));
            Assert.That(response.RealizeInvoked, Is.False);
            Assert.That(response.PostconditionVerified, Is.True);
            Assert.That(response.FindItemByPropertyCalls, Is.EqualTo(1));
            Assert.That(response.PostconditionPolls, Is.EqualTo(1));
            Assert.That(response.ViewportMayHaveChanged, Is.False);
            Assert.That(response.DataOrContainerLoadingMayHaveOccurred, Is.False);
            Assert.That(response.Reusable, Is.True);
            Assert.That(provider.RealizedTargets, Is.Empty);
        });
    }

    [Test]
    public async Task Exact_name_probes_second_match_then_reacquires_before_realization()
    {
        const string exactName = "  Item ALPHA  ";
        var firstPlaceholder = new FakeItem("first-placeholder");
        var reacquiredPlaceholder = new FakeItem("reacquired-placeholder");
        var provider = new FakeProvider
        {
            FindByExactNameHandler = (startAfter, call) => call switch
            {
                1 when startAfter is null => firstPlaceholder,
                2 when startAfter == firstPlaceholder => null,
                3 when startAfter is null => reacquiredPlaceholder,
                _ => throw new AssertionException("Unexpected exact-Name provider call.")
            },
            TargetStateHandler = _ => RealizeItemTargetState.Virtualized,
            PostconditionHandler = (_, _) => ValueTask.FromResult(
                RealizeItemPostconditionResult.Verified(Element("alpha"), reusable: false))
        };

        var response = await ExecuteAsync(new RealizeItemRequest(Name: exactName), provider);

        Assert.Multiple(() =>
        {
            Assert.That(provider.ExactNameArguments, Is.EqualTo(new[] { exactName, exactName, exactName }));
            Assert.That(provider.RealizedTargets, Is.EqualTo(new[] { reacquiredPlaceholder }));
            Assert.That(response.RequestedIdentity, Is.EqualTo(new RealizeItemRequestedIdentity(Name: exactName)));
            Assert.That(response.FindItemByPropertyCalls, Is.EqualTo(3));
            Assert.That(response.StopReason, Is.EqualTo(RealizeItemOutcomes.StopCompleted));
        });
    }

    [Test]
    public async Task Whitespace_only_name_is_preserved_as_an_exact_provider_value()
    {
        const string exactName = "   ";
        var first = new FakeItem("first");
        var reacquired = new FakeItem("reacquired");
        var provider = new FakeProvider
        {
            FindByExactNameHandler = (_, call) => call switch
            {
                1 => first,
                2 => null,
                3 => reacquired,
                _ => throw new AssertionException("Unexpected exact-Name provider call.")
            },
            TargetStateHandler = _ => RealizeItemTargetState.AlreadyRealized,
            PostconditionHandler = (_, _) => ValueTask.FromResult(
                RealizeItemPostconditionResult.Verified(Element(exactName), reusable: false))
        };

        var response = await ExecuteAsync(new RealizeItemRequest(Name: exactName), provider);

        Assert.Multiple(() =>
        {
            Assert.That(provider.ExactNameArguments, Is.EqualTo(new[] { exactName, exactName, exactName }));
            Assert.That(response.RequestedIdentity.Name, Is.EqualTo(exactName));
            Assert.That(response.StopReason, Is.EqualTo(RealizeItemOutcomes.StopCompleted));
        });
    }

    [Test]
    public async Task Provider_observed_second_name_match_is_ambiguous_without_mutation()
    {
        var first = new FakeItem("first");
        var second = new FakeItem("second");
        var provider = new FakeProvider
        {
            FindByExactNameHandler = (_, call) => call == 1 ? first : second
        };

        var response = await ExecuteAsync(new RealizeItemRequest(Name: "Duplicate"), provider);

        Assert.Multiple(() =>
        {
            Assert.That(response.StopReason, Is.EqualTo(RealizeItemOutcomes.StopAmbiguous));
            Assert.That(response.MethodUsed, Is.EqualTo(RealizeItemOutcomes.MethodNone));
            Assert.That(response.FindItemByPropertyCalls, Is.EqualTo(2));
            Assert.That(response.RealizeInvoked, Is.False);
            Assert.That(response.PostconditionVerified, Is.False);
            Assert.That(response.Reusable, Is.False);
            Assert.That(provider.RealizedTargets, Is.Empty);
        });
    }

    [Test]
    public async Task Provider_call_limit_stops_index_enumeration_before_the_next_call()
    {
        var call = 0;
        var provider = new FakeProvider
        {
            FindNextHandler = _ => new FakeItem($"item-{++call}")
        };

        var response = await ExecuteAsync(
            new RealizeItemRequest(Index: 2, MaxProviderCalls: 2),
            provider);

        Assert.Multiple(() =>
        {
            Assert.That(response.StopReason, Is.EqualTo(RealizeItemOutcomes.StopProviderCallLimit));
            Assert.That(response.FindItemByPropertyCalls, Is.EqualTo(2));
            Assert.That(provider.FindNextStarts, Has.Count.EqualTo(2));
            Assert.That(response.RealizeInvoked, Is.False);
        });
    }

    [Test]
    public async Task Elapsed_limit_is_checked_between_provider_calls_not_inside_one_call()
    {
        var clock = new ManualTimeProvider(ClockOrigin);
        var provider = new FakeProvider
        {
            FindNextHandler = _ =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(50));
                return new FakeItem("first");
            }
        };

        var response = await ExecuteAsync(
            new RealizeItemRequest(Index: 1, AdvisoryElapsedLimitMs: 50),
            provider,
            clock);

        Assert.Multiple(() =>
        {
            Assert.That(response.StopReason, Is.EqualTo(RealizeItemOutcomes.StopAdvisoryElapsedLimit));
            Assert.That(response.FindItemByPropertyCalls, Is.EqualTo(1));
            Assert.That(response.ElapsedMs, Is.EqualTo(50));
        });
    }

    [Test]
    public async Task Unsupported_out_of_tree_placeholder_is_reported_without_realization()
    {
        var provider = new FakeProvider
        {
            FindNextHandler = _ => new FakeItem("placeholder"),
            TargetStateHandler = _ => RealizeItemTargetState.Unsupported
        };

        var response = await ExecuteAsync(new RealizeItemRequest(Index: 0), provider);

        Assert.Multiple(() =>
        {
            Assert.That(response.StopReason, Is.EqualTo(RealizeItemOutcomes.StopUnsupported));
            Assert.That(response.MethodUsed, Is.EqualTo(RealizeItemOutcomes.MethodNone));
            Assert.That(response.RealizeInvoked, Is.False);
            Assert.That(provider.RealizedTargets, Is.Empty);
        });
    }

    [Test]
    public void Cancellation_between_find_calls_propagates_before_any_mutation()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeProvider
        {
            FindNextHandler = _ =>
            {
                cancellation.Cancel();
                return new FakeItem("first");
            }
        };

        Assert.ThrowsAsync<OperationCanceledException>(() => ExecuteAsync(
            new RealizeItemRequest(Index: 1),
            provider,
            cancellationToken: cancellation.Token));

        Assert.Multiple(() =>
        {
            Assert.That(provider.FindNextStarts, Has.Count.EqualTo(1));
            Assert.That(provider.RealizedTargets, Is.Empty);
        });
    }

    [Test]
    public async Task Cancellation_after_realize_returns_mutation_evidence()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeProvider
        {
            FindNextHandler = _ => new FakeItem("target"),
            TargetStateHandler = _ => RealizeItemTargetState.Virtualized,
            RealizeHandler = _ => cancellation.Cancel(),
            PostconditionHandler = (_, _) => ValueTask.FromResult(
                RealizeItemPostconditionResult.Pending())
        };

        var response = await ExecuteAsync(
            new RealizeItemRequest(Index: 0),
            provider,
            cancellationToken: cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(response.StopReason, Is.EqualTo(RealizeItemOutcomes.StopCancelledAfterRealize));
            Assert.That(response.MethodUsed, Is.EqualTo(RealizeItemOutcomes.MethodVirtualizedItemRealize));
            Assert.That(response.RealizeInvoked, Is.True);
            Assert.That(response.PostconditionPolls, Is.EqualTo(1));
            Assert.That(response.PostconditionVerified, Is.False);
            Assert.That(response.ViewportMayHaveChanged, Is.True);
            Assert.That(response.DataOrContainerLoadingMayHaveOccurred, Is.True);
            Assert.That(response.Reusable, Is.False);
        });
    }

    [Test]
    public async Task Realize_failure_keeps_invocation_and_mutation_evidence()
    {
        var provider = new FakeProvider
        {
            FindNextHandler = _ => new FakeItem("target"),
            TargetStateHandler = _ => RealizeItemTargetState.Virtualized,
            RealizeHandler = _ => throw new InvalidOperationException("provider failed")
        };

        var response = await ExecuteAsync(new RealizeItemRequest(Index: 0), provider);

        Assert.Multiple(() =>
        {
            Assert.That(response.StopReason, Is.EqualTo(RealizeItemOutcomes.StopRealizeFailure));
            Assert.That(response.RealizeInvoked, Is.True);
            Assert.That(response.ViewportMayHaveChanged, Is.True);
            Assert.That(response.DataOrContainerLoadingMayHaveOccurred, Is.True);
            Assert.That(response.Failure?.Detail, Is.EqualTo("provider failed"));
            Assert.That(response.PostconditionPolls, Is.Zero);
        });
    }

    [Test]
    public async Task Postcondition_failure_keeps_mutation_evidence_and_suppresses_reuse()
    {
        var provider = new FakeProvider
        {
            FindNextHandler = _ => new FakeItem("target"),
            TargetStateHandler = _ => RealizeItemTargetState.Virtualized,
            PostconditionHandler = (_, _) => ValueTask.FromException<RealizeItemPostconditionResult>(
                new InvalidOperationException("identity read failed"))
        };

        var response = await ExecuteAsync(new RealizeItemRequest(Index: 0), provider);

        Assert.Multiple(() =>
        {
            Assert.That(response.StopReason, Is.EqualTo(RealizeItemOutcomes.StopPostconditionFailure));
            Assert.That(response.RealizeInvoked, Is.True);
            Assert.That(response.PostconditionPolls, Is.EqualTo(1));
            Assert.That(response.PostconditionVerified, Is.False);
            Assert.That(response.Reusable, Is.False);
            Assert.That(response.Failure?.Detail, Is.EqualTo("identity read failed"));
        });
    }

    [Test]
    public async Task Terminal_identity_failure_suppresses_handle_without_erasing_realize_evidence()
    {
        var provider = new FakeProvider
        {
            FindNextHandler = _ => new FakeItem("target"),
            TargetStateHandler = _ => RealizeItemTargetState.Virtualized,
            PostconditionHandler = (_, _) => ValueTask.FromResult(
                RealizeItemPostconditionResult.Terminal(
                    RealizeItemOutcomes.StopIdentityChanged,
                    "Resolve the item again before interacting with it."))
        };

        var response = await ExecuteAsync(new RealizeItemRequest(Index: 0), provider);

        Assert.Multiple(() =>
        {
            Assert.That(response.StopReason, Is.EqualTo(RealizeItemOutcomes.StopIdentityChanged));
            Assert.That(response.RecoveryReason, Does.Contain("Resolve the item again"));
            Assert.That(response.RealizeInvoked, Is.True);
            Assert.That(response.PostconditionVerified, Is.False);
            Assert.That(response.Reusable, Is.False);
            Assert.That(response.Element, Is.Null);
        });
    }

    [Test]
    public async Task Registration_failure_keeps_verified_element_metadata_and_realization_evidence()
    {
        var element = Element("target");
        var failure = new FailureInfo("registration_failed", "identity", "Local handle registration failed.");
        var provider = new FakeProvider
        {
            FindNextHandler = _ => new FakeItem("target"),
            TargetStateHandler = _ => RealizeItemTargetState.Virtualized,
            PostconditionHandler = (_, _) => ValueTask.FromResult(
                RealizeItemPostconditionResult.Verified(
                    element,
                    reusable: false,
                    stopReason: RealizeItemOutcomes.StopRegistrationFailed,
                    recoveryReason: "Resolve the verified item again to obtain a reusable handle.",
                    failure: failure))
        };

        var response = await ExecuteAsync(new RealizeItemRequest(Index: 0), provider);

        Assert.Multiple(() =>
        {
            Assert.That(response.StopReason, Is.EqualTo(RealizeItemOutcomes.StopRegistrationFailed));
            Assert.That(response.RealizeInvoked, Is.True);
            Assert.That(response.PostconditionVerified, Is.True);
            Assert.That(response.Element, Is.SameAs(element));
            Assert.That(response.Reusable, Is.False);
            Assert.That(response.Failure, Is.SameAs(failure));
        });
    }

    [Test]
    public async Task Find_call_is_counted_before_a_provider_exception()
    {
        var provider = new FakeProvider
        {
            FindNextHandler = _ => throw new InvalidOperationException("find failed")
        };

        var response = await ExecuteAsync(new RealizeItemRequest(Index: 0), provider);

        Assert.Multiple(() =>
        {
            Assert.That(response.StopReason, Is.EqualTo(RealizeItemOutcomes.StopProviderFailure));
            Assert.That(response.FindItemByPropertyCalls, Is.EqualTo(1));
            Assert.That(response.Failure?.Detail, Is.EqualTo("find failed"));
            Assert.That(response.RealizeInvoked, Is.False);
        });
    }

    private static Task<RealizeItemResponse> ExecuteAsync(
        RealizeItemRequest request,
        FakeProvider provider,
        ManualTimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        CancellationToken cancellationToken = default) =>
        RealizeItemCoordinator.ExecuteAsync(
            request,
            windowHandleUsed: 42,
            provider,
            exception => new FailureInfo("realize_item_failed", "uia", exception.Message),
            cancellationToken,
            clock,
            delayAsync);

    private static ElementRef Element(string name, string? elementId = null) =>
        new(
            Type: "ListBoxItem",
            AutomationId: null,
            Name: name,
            XPath: $"/ListBox/ListBoxItem[@Name='{name}']",
            ElementId: elementId);

    private sealed record FakeItem(string Id);

    private sealed class FakeProvider : IRealizeItemProvider<FakeItem>
    {
        private int _exactNameCalls;
        private int _postconditionCalls;

        public Func<FakeItem?, FakeItem?> FindNextHandler { get; init; } = _ => null;

        public Func<FakeItem?, int, FakeItem?> FindByExactNameHandler { get; init; } = (_, _) => null;

        public Func<FakeItem, RealizeItemTargetState> TargetStateHandler { get; init; } =
            _ => RealizeItemTargetState.Unsupported;

        public Action<FakeItem> RealizeHandler { get; init; } = _ => { };

        public Func<FakeItem, int, ValueTask<RealizeItemPostconditionResult>> PostconditionHandler { get; init; } =
            (_, _) => ValueTask.FromResult(RealizeItemPostconditionResult.Pending());

        public List<FakeItem?> FindNextStarts { get; } = [];

        public List<string> ExactNameArguments { get; } = [];

        public List<FakeItem> RealizedTargets { get; } = [];

        public List<FakeItem> PostconditionTargets { get; } = [];

        public FakeItem? FindNext(FakeItem? startAfter)
        {
            FindNextStarts.Add(startAfter);
            return FindNextHandler(startAfter);
        }

        public FakeItem? FindByExactName(FakeItem? startAfter, string exactName)
        {
            ExactNameArguments.Add(exactName);
            return FindByExactNameHandler(startAfter, ++_exactNameCalls);
        }

        public RealizeItemTargetState GetTargetState(FakeItem target) =>
            TargetStateHandler(target);

        public void Realize(FakeItem target)
        {
            RealizedTargets.Add(target);
            RealizeHandler(target);
        }

        public ValueTask<RealizeItemPostconditionResult> CheckPostconditionAsync(FakeItem target)
        {
            PostconditionTargets.Add(target);
            return PostconditionHandler(target, ++_postconditionCalls);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset origin) : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000;

        public override DateTimeOffset GetUtcNow() =>
            origin + TimeSpan.FromMilliseconds(GetTimestamp());

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsed));
            }

            _ = Interlocked.Add(ref _timestamp, checked((long)elapsed.TotalMilliseconds));
        }
    }
}
