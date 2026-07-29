using System.Text.Json.Nodes;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class DiagnosticSnapshotCoordinatorTests
{
    private static readonly DateTimeOffset ClockOrigin =
        new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void Validator_enforces_target_options_and_normalizes_valid_input()
    {
        Assert.Throws<ArgumentException>(() => DiagnosticSnapshotRequestValidator.Validate(
            CreateRequest(
                locator: new ElementLocator(AutomationId: "Target"),
                elementId: "element-1")));
        Assert.Throws<ArgumentException>(() => DiagnosticSnapshotRequestValidator.Validate(
            CreateRequest(elementId: "   ")));

        var windowRequest = DiagnosticSnapshotRequestValidator.Validate(
            CreateRequest(sessionId: "  session-1  "));
        var elementRequest = DiagnosticSnapshotRequestValidator.Validate(
            CreateRequest(elementId: "  element-1  "));

        Assert.Multiple(() =>
        {
            Assert.That(windowRequest.SessionId, Is.EqualTo("session-1"));
            Assert.That(windowRequest.Locator, Is.Null);
            Assert.That(windowRequest.ElementId, Is.Null);
            Assert.That(elementRequest.ElementId, Is.EqualTo("element-1"));
        });
    }

    [Test]
    public void Validator_requires_between_one_and_eight_unique_supported_sections()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DiagnosticSnapshotRequestValidator.Validate(
            CreateRequest(sections: [])));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiagnosticSnapshotRequestValidator.Validate(
            CreateRequest(sections: Enumerable.Repeat(DiagnosticSection.VisualTree, 9).ToArray())));
        Assert.Throws<ArgumentException>(() => DiagnosticSnapshotRequestValidator.Validate(
            CreateRequest(sections: [DiagnosticSection.VisualTree, DiagnosticSection.VisualTree])));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiagnosticSnapshotRequestValidator.Validate(
            CreateRequest(sections: [(DiagnosticSection)int.MaxValue])));

        var requested = new[]
        {
            DiagnosticSection.Screenshot,
            DiagnosticSection.BindingErrors,
            DiagnosticSection.DataContext,
            DiagnosticSection.Bindings,
            DiagnosticSection.Layout,
            DiagnosticSection.WpfProperties,
            DiagnosticSection.UiaProperties,
            DiagnosticSection.VisualTree
        };
        var validated = DiagnosticSnapshotRequestValidator.Validate(
            CreateRequest(sections: requested, propertyNames: ["Text"]));

        Assert.That(validated.Sections, Is.EqualTo(requested));
    }

    [Test]
    public void Validator_enforces_budget_and_timeout_bounds()
    {
        var invalidRequests = new[]
        {
            CreateRequest(budget: new DiagnosticSnapshotBudget(MaxDepth: 0)),
            CreateRequest(budget: new DiagnosticSnapshotBudget(MaxDepth: 7)),
            CreateRequest(budget: new DiagnosticSnapshotBudget(MaxItems: 0)),
            CreateRequest(budget: new DiagnosticSnapshotBudget(MaxItems: 101)),
            CreateRequest(budget: new DiagnosticSnapshotBudget(MaxNodes: 0)),
            CreateRequest(budget: new DiagnosticSnapshotBudget(MaxNodes: 1_001)),
            CreateRequest(budget: new DiagnosticSnapshotBudget(MaxValueLength: 63)),
            CreateRequest(budget: new DiagnosticSnapshotBudget(MaxValueLength: 2_001)),
            CreateRequest(budget: new DiagnosticSnapshotBudget(MaxPayloadChars: 999)),
            CreateRequest(budget: new DiagnosticSnapshotBudget(MaxPayloadChars: 100_001)),
            CreateRequest(timeoutMs: 99),
            CreateRequest(timeoutMs: 30_001)
        };

        foreach (var request in invalidRequests)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                DiagnosticSnapshotRequestValidator.Validate(request));
        }
    }

    [Test]
    public void Validator_enforces_property_name_bounds_and_normalizes_names()
    {
        var tooManyNames = Enumerable.Range(0, DiagnosticSnapshotLimits.MaxPropertyNames + 1)
            .Select(index => $"Property{index}")
            .ToArray();
        var tooLongName = new string('p', DiagnosticSnapshotLimits.MaxPropertyNameLength + 1);
        var invalidPropertyRequests = new[]
        {
            CreateRequest(
                sections: [DiagnosticSection.WpfProperties],
                propertyNames: []),
            CreateRequest(
                sections: [DiagnosticSection.WpfProperties],
                propertyNames: tooManyNames),
            CreateRequest(
                sections: [DiagnosticSection.WpfProperties],
                propertyNames: ["Text", " "]),
            CreateRequest(
                sections: [DiagnosticSection.WpfProperties],
                propertyNames: [tooLongName]),
            CreateRequest(
                sections: [DiagnosticSection.WpfProperties],
                propertyNames: ["Text", " Text "])
        };
        var invalidDataContextRequests = new[]
        {
            CreateRequest(
                sections: [DiagnosticSection.DataContext],
                dataContextProperties: []),
            CreateRequest(
                sections: [DiagnosticSection.DataContext],
                dataContextProperties: tooManyNames),
            CreateRequest(
                sections: [DiagnosticSection.DataContext],
                dataContextProperties: [tooLongName]),
            CreateRequest(
                sections: [DiagnosticSection.DataContext],
                dataContextProperties: ["Status", " Status "])
        };

        foreach (var request in invalidPropertyRequests.Concat(invalidDataContextRequests))
        {
            Assert.Catch<ArgumentException>(() =>
                DiagnosticSnapshotRequestValidator.Validate(request));
        }

        var validated = DiagnosticSnapshotRequestValidator.Validate(
            CreateRequest(
                sections: [DiagnosticSection.WpfProperties, DiagnosticSection.DataContext],
                propertyNames: [" Text ", "Visibility"],
                dataContextProperties: [" Status "]));

        Assert.Multiple(() =>
        {
            Assert.That(validated.PropertyNames, Is.EqualTo(new[] { "Text", "Visibility" }));
            Assert.That(validated.DataContextProperties, Is.EqualTo(new[] { "Status" }));
        });
    }

    [Test]
    public void Validator_requires_wpf_property_names_and_rejects_irrelevant_name_options()
    {
        Assert.Throws<ArgumentException>(() => DiagnosticSnapshotRequestValidator.Validate(
            CreateRequest(sections: [DiagnosticSection.WpfProperties])));
        Assert.Throws<ArgumentException>(() => DiagnosticSnapshotRequestValidator.Validate(
            CreateRequest(
                sections: [DiagnosticSection.VisualTree],
                propertyNames: ["Text"])));
        Assert.Throws<ArgumentException>(() => DiagnosticSnapshotRequestValidator.Validate(
            CreateRequest(
                sections: [DiagnosticSection.VisualTree],
                dataContextProperties: ["Status"])));

        Assert.DoesNotThrow(() => DiagnosticSnapshotRequestValidator.Validate(
            CreateRequest(
                sections: [DiagnosticSection.WpfProperties, DiagnosticSection.DataContext],
                propertyNames: ["Text"],
                dataContextProperties: ["Status"])));
    }

    [Test]
    public async Task Coordinator_preserves_requested_order()
    {
        var clock = new ManualTimeProvider(ClockOrigin);
        var requested = new[]
        {
            DiagnosticSection.Screenshot,
            DiagnosticSection.VisualTree,
            DiagnosticSection.DataContext
        };

        var results = await CaptureAsync(
            requested,
            clock,
            (section, _) => Task.FromResult(Evidence(section.ToString())));

        Assert.That(results.Select(result => result.Section), Is.EqualTo(requested));
    }

    [Test]
    public async Task Coordinator_reports_monotonic_offsets_for_changing_external_state()
    {
        var clock = new ManualTimeProvider(ClockOrigin);
        var externalState = 0;
        var requested = new[]
        {
            DiagnosticSection.VisualTree,
            DiagnosticSection.UiaProperties,
            DiagnosticSection.Screenshot
        };

        var results = await CaptureAsync(
            requested,
            clock,
            (_, _) =>
            {
                externalState++;
                clock.Advance(TimeSpan.FromMilliseconds(externalState + 2));
                return Task.FromResult(Evidence(externalState));
            });

        Assert.Multiple(() =>
        {
            Assert.That(
                results.Select(result => result.Data!.GetValue<int>()),
                Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(
                results.Select(result => result.StartedOffsetMs),
                Is.EqualTo(new long[] { 0, 3, 7 }));
            Assert.That(
                results.Select(result => result.CompletedOffsetMs),
                Is.EqualTo(new long[] { 3, 7, 12 }));
            Assert.That(
                results.Select(result => result.DurationMs),
                Is.EqualTo(new long[] { 3, 4, 5 }));
            Assert.That(
                results.Select(result => result.StartedAtUtc),
                Is.Ordered.Ascending);
            Assert.That(
                results.Select(result => result.CompletedAtUtc),
                Is.Ordered.Ascending);
        });
    }

    [Test]
    public async Task Coordinator_keeps_later_success_after_one_section_fails()
    {
        var clock = new ManualTimeProvider(ClockOrigin);
        var requested = new[]
        {
            DiagnosticSection.UiaProperties,
            DiagnosticSection.Screenshot
        };

        var results = await CaptureAsync(
            requested,
            clock,
            (section, _) => section == DiagnosticSection.UiaProperties
                ? Task.FromException<DiagnosticSectionEvidence>(new InvalidOperationException("provider failed"))
                : Task.FromResult(Evidence("captured")),
            exception => new DiagnosticSectionFailure(
                DiagnosticSectionStatus.Failed,
                "capture_failed",
                exception.Message));

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results[0].Status, Is.EqualTo(DiagnosticSectionStatus.Failed));
            Assert.That(results[0].Code, Is.EqualTo("capture_failed"));
            Assert.That(results[0].Data, Is.Null);
            Assert.That(results[1].Status, Is.EqualTo(DiagnosticSectionStatus.Success));
            Assert.That(results[1].Data!.GetValue<string>(), Is.EqualTo("captured"));
        });
    }

    [Test]
    public async Task Coordinator_promotes_truncated_evidence_to_section_status()
    {
        var clock = new ManualTimeProvider(ClockOrigin);

        var results = await CaptureAsync(
            [DiagnosticSection.BindingErrors],
            clock,
            (_, _) => Task.FromResult(new DiagnosticSectionEvidence(
                JsonValue.Create("partial"),
                Truncated: true,
                Code: "maxItems",
                Message: "Only the bounded prefix was returned.")));

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Status, Is.EqualTo(DiagnosticSectionStatus.Truncated));
            Assert.That(results[0].Data!.GetValue<string>(), Is.EqualTo("partial"));
            Assert.That(results[0].Code, Is.EqualTo("maxItems"));
            Assert.That(results[0].Message, Does.Contain("bounded prefix"));
        });
    }

    [Test]
    public void Coordinator_propagates_cancellation_instead_of_classifying_it_as_a_section_failure()
    {
        var clock = new ManualTimeProvider(ClockOrigin);
        using var cancellation = new CancellationTokenSource();
        var captureCalls = 0;
        var classifierCalls = 0;

        var exception = Assert.ThrowsAsync<OperationCanceledException>(async () => await CaptureAsync(
            [DiagnosticSection.VisualTree, DiagnosticSection.Screenshot],
            clock,
            (_, _) =>
            {
                captureCalls++;
                cancellation.Cancel();
                return Task.FromException<DiagnosticSectionEvidence>(
                    new OperationCanceledException(cancellation.Token));
            },
            exception =>
            {
                classifierCalls++;
                return new DiagnosticSectionFailure(
                    DiagnosticSectionStatus.Failed,
                    "incorrectly_classified",
                    exception.Message);
            },
            cancellation.Token));

        Assert.Multiple(() =>
        {
            Assert.That(exception, Is.Not.Null);
            Assert.That(captureCalls, Is.EqualTo(1));
            Assert.That(classifierCalls, Is.Zero);
        });
    }

    [Test]
    public async Task Payload_budget_retains_earlier_evidence_and_marks_later_evidence_truncated()
    {
        var clock = new ManualTimeProvider(ClockOrigin);
        var captured = await CaptureAsync(
            [DiagnosticSection.VisualTree, DiagnosticSection.UiaProperties],
            clock,
            (section, _) => Task.FromResult(Evidence(new
            {
                Section = section.ToString(),
                Value = new string(section == DiagnosticSection.VisualTree ? 'a' : 'b', 20)
            })));
        var firstPayloadLength = captured[0].Data!.ToJsonString().Length;

        var bounded = DiagnosticSnapshotCoordinator.ApplyPayloadBudget(
            captured,
            firstPayloadLength);

        Assert.Multiple(() =>
        {
            Assert.That(bounded.Select(result => result.Section), Is.EqualTo(captured.Select(result => result.Section)));
            Assert.That(bounded[0].Status, Is.EqualTo(DiagnosticSectionStatus.Success));
            Assert.That(bounded[0].Data, Is.Not.Null);
            Assert.That(bounded[0].PayloadChars, Is.EqualTo(firstPayloadLength));
            Assert.That(bounded[1].Status, Is.EqualTo(DiagnosticSectionStatus.Truncated));
            Assert.That(bounded[1].Data, Is.Null);
            Assert.That(bounded[1].Code, Is.EqualTo("maxPayloadChars"));
            Assert.That(bounded[1].Message, Does.Contain("remaining payload budget was 0"));
            Assert.That(bounded[1].PayloadChars, Is.Zero);
        });
    }

    private static CaptureDiagnosticSnapshotRequest CreateRequest(
        string sessionId = "session-1",
        IReadOnlyList<DiagnosticSection>? sections = null,
        ElementLocator? locator = null,
        string? elementId = null,
        DiagnosticSnapshotBudget? budget = null,
        IReadOnlyList<string>? propertyNames = null,
        IReadOnlyList<string>? dataContextProperties = null,
        int timeoutMs = 10_000) =>
        new(
            SessionId: sessionId,
            Sections: sections ?? [DiagnosticSection.VisualTree],
            Locator: locator,
            ElementId: elementId,
            Budget: budget,
            PropertyNames: propertyNames,
            DataContextProperties: dataContextProperties,
            TimeoutMs: timeoutMs);

    private static Task<IReadOnlyList<DiagnosticSectionResult>> CaptureAsync(
        IReadOnlyList<DiagnosticSection> sections,
        ManualTimeProvider clock,
        Func<DiagnosticSection, CancellationToken, Task<DiagnosticSectionEvidence>> capture,
        Func<Exception, DiagnosticSectionFailure>? classifyFailure = null,
        CancellationToken cancellationToken = default)
    {
        var captureStartedAtUtc = clock.GetUtcNow();
        var captureStartedTimestamp = clock.GetTimestamp();
        return DiagnosticSnapshotCoordinator.CaptureAsync(
            sections,
            captureStartedAtUtc,
            captureStartedTimestamp,
            section => section switch
            {
                DiagnosticSection.UiaProperties => DiagnosticCaptureSource.Uia,
                DiagnosticSection.Screenshot => DiagnosticCaptureSource.Screenshot,
                _ => DiagnosticCaptureSource.WpfDispatcher
            },
            section => $"{section}:v1",
            section => $"capture-{section}",
            capture,
            classifyFailure ?? (exception => new DiagnosticSectionFailure(
                DiagnosticSectionStatus.Failed,
                "capture_failed",
                exception.Message)),
            cancellationToken,
            clock);
    }

    private static DiagnosticSectionEvidence Evidence<T>(T value) =>
        new(DiagnosticSnapshotCoordinator.SerializeEvidence(value));

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
