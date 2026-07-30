using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal static class ElementMappingScoring
{
    internal const int MinimumHeuristicScore = 150;
    internal const int MinimumHeuristicLead = 40;

    internal sealed record Facts(
        string? AutomationId,
        string? Name,
        string? ClassName,
        Rect? Bounds);

    internal sealed record CandidateScore(
        int Score,
        bool AutomationIdExact,
        bool TypeCompatible,
        bool Reusable,
        IReadOnlyList<string> Evidence);

    internal sealed record Decision(
        ElementMappingStatus Status,
        int? SelectedIndex,
        int? Score,
        int? ScoreLead,
        IReadOnlyList<string> Evidence);

    internal static CandidateScore? Score(
        Facts source,
        Facts candidate,
        bool typeCompatible,
        bool reusable)
    {
        var score = 0;
        var evidence = new List<string>();
        var automationIdExact = false;

        if (!string.IsNullOrWhiteSpace(source.AutomationId))
        {
            if (string.IsNullOrWhiteSpace(candidate.AutomationId))
            {
                evidence.Add("automation_id_missing");
            }
            else if (!string.Equals(candidate.AutomationId, source.AutomationId, StringComparison.Ordinal))
            {
                return null;
            }
            else
            {
                automationIdExact = true;
                score += 100;
                evidence.Add("automation_id_exact");
            }
        }

        if (typeCompatible)
        {
            score += 40;
            evidence.Add("control_type_compatible");
        }

        if (!string.IsNullOrWhiteSpace(source.Name) &&
            string.Equals(candidate.Name, source.Name, StringComparison.Ordinal))
        {
            score += 30;
            evidence.Add("name_exact");
        }

        if (!string.IsNullOrWhiteSpace(source.ClassName) &&
            string.Equals(candidate.ClassName, source.ClassName, StringComparison.Ordinal))
        {
            score += 20;
            evidence.Add("class_name_exact");
        }

        if (source.Bounds is { } sourceBounds && candidate.Bounds is { } candidateBounds)
        {
            var boundsScore = ScoreBounds(candidateBounds, sourceBounds);
            if (boundsScore > 0)
            {
                score += boundsScore;
                evidence.Add($"bounds_match_{boundsScore}");
            }
        }

        evidence.Add(reusable ? "runtime_identity_available" : "runtime_identity_unavailable");
        return score > 0
            ? new CandidateScore(score, automationIdExact, typeCompatible, reusable, evidence)
            : null;
    }

    internal static Decision Decide(
        IReadOnlyList<CandidateScore> orderedCandidates,
        bool scanComplete)
    {
        var top = orderedCandidates.Count > 0 ? orderedCandidates[0] : null;
        int? scoreLead = orderedCandidates.Count > 1 && top is not null
            ? top.Score - orderedCandidates[1].Score
            : null;

        if (!scanComplete)
        {
            return new Decision(
                ElementMappingStatus.Ambiguous,
                SelectedIndex: null,
                Score: top?.Score,
                ScoreLead: scoreLead,
                Evidence: ["scan_incomplete"]);
        }

        if (top is null)
        {
            return new Decision(
                ElementMappingStatus.Unmapped,
                SelectedIndex: null,
                Score: null,
                ScoreLead: null,
                Evidence: ["scan_complete", "no_relevant_candidates"]);
        }

        if (scoreLead == 0)
        {
            return new Decision(
                ElementMappingStatus.Ambiguous,
                SelectedIndex: null,
                Score: top.Score,
                ScoreLead: 0,
                Evidence: ["scan_complete", "top_score_tied"]);
        }

        var exactCandidateCount = orderedCandidates.Count(candidate =>
            candidate.AutomationIdExact && candidate.TypeCompatible);
        if (top.AutomationIdExact && top.TypeCompatible && top.Reusable && exactCandidateCount == 1)
        {
            return new Decision(
                ElementMappingStatus.Exact,
                SelectedIndex: 0,
                Score: top.Score,
                ScoreLead: scoreLead,
                Evidence: [
                    "scan_complete",
                    "unique_top_score",
                    "unique_exact_automation_id_and_control_type",
                    "runtime_identity_available"
                ]);
        }

        if (exactCandidateCount > 0 && !(top.AutomationIdExact && top.TypeCompatible))
        {
            return new Decision(
                ElementMappingStatus.Ambiguous,
                SelectedIndex: null,
                Score: top.Score,
                ScoreLead: scoreLead,
                Evidence: ["scan_complete", "exact_identity_not_top_ranked"]);
        }

        if (!top.Reusable)
        {
            return new Decision(
                ElementMappingStatus.Ambiguous,
                SelectedIndex: null,
                Score: top.Score,
                ScoreLead: scoreLead,
                Evidence: ["scan_complete", "runtime_identity_unavailable"]);
        }

        if (top.Score < MinimumHeuristicScore)
        {
            return new Decision(
                ElementMappingStatus.Ambiguous,
                SelectedIndex: null,
                Score: top.Score,
                ScoreLead: scoreLead,
                Evidence: ["scan_complete", "score_below_heuristic_threshold"]);
        }

        if (scoreLead is int lead && lead < MinimumHeuristicLead)
        {
            return new Decision(
                ElementMappingStatus.Ambiguous,
                SelectedIndex: null,
                Score: top.Score,
                ScoreLead: lead,
                Evidence: ["scan_complete", "score_lead_below_heuristic_threshold"]);
        }

        var decisionEvidence = new List<string>
        {
            "scan_complete",
            "unique_top_score",
            "score_threshold_met",
            scoreLead is null ? "single_candidate" : "score_lead_threshold_met",
            "runtime_identity_available"
        };
        if (exactCandidateCount > 1)
        {
            decisionEvidence.Add("exact_identity_not_unique");
        }

        return new Decision(
            ElementMappingStatus.Heuristic,
            SelectedIndex: 0,
            Score: top.Score,
            ScoreLead: scoreLead,
            Evidence: decisionEvidence);
    }

    private static int ScoreBounds(Rect candidate, Rect expected)
    {
        if (!HasUsableBounds(candidate) || !HasUsableBounds(expected))
        {
            return 0;
        }

        var left = Math.Max(candidate.X, expected.X);
        var top = Math.Max(candidate.Y, expected.Y);
        var right = Math.Min(candidate.X + candidate.Width, expected.X + expected.Width);
        var bottom = Math.Min(candidate.Y + candidate.Height, expected.Y + expected.Height);

        if (right > left && bottom > top)
        {
            var intersection = (right - left) * (bottom - top);
            var candidateArea = candidate.Width * candidate.Height;
            var expectedArea = expected.Width * expected.Height;
            var union = candidateArea + expectedArea - intersection;
            var iou = union > 0 ? intersection / union : 0;
            var expectedCoverage = expectedArea > 0 ? intersection / expectedArea : 0;
            var candidateCoverage = candidateArea > 0 ? intersection / candidateArea : 0;

            if (iou >= 0.85)
            {
                return 140;
            }

            if (iou >= 0.6)
            {
                return 100;
            }

            if (expectedCoverage >= 0.9 && candidateCoverage >= 0.7)
            {
                return 80;
            }

            if (expectedCoverage >= 0.9 || candidateCoverage >= 0.9)
            {
                return 25;
            }
        }

        var candidateCenterX = candidate.X + candidate.Width / 2.0;
        var candidateCenterY = candidate.Y + candidate.Height / 2.0;
        var expectedCenterX = expected.X + expected.Width / 2.0;
        var expectedCenterY = expected.Y + expected.Height / 2.0;
        var distance = Math.Sqrt(
            Math.Pow(candidateCenterX - expectedCenterX, 2) +
            Math.Pow(candidateCenterY - expectedCenterY, 2));
        var widthSimilarity = Math.Min(candidate.Width, expected.Width) / Math.Max(candidate.Width, expected.Width);
        var heightSimilarity = Math.Min(candidate.Height, expected.Height) / Math.Max(candidate.Height, expected.Height);
        var sizeSimilarity = Math.Min(widthSimilarity, heightSimilarity);

        if (distance <= 4 && sizeSimilarity >= 0.8)
        {
            return 100;
        }

        if (distance <= 16 && sizeSimilarity >= 0.6)
        {
            return 60;
        }

        return distance <= 48 && sizeSimilarity >= 0.4 ? 20 : 0;
    }

    private static bool HasUsableBounds(Rect bounds) =>
        bounds.Width > 0 && bounds.Height > 0;
}
