using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arbitrage.Application;
using Arbitrage.Domain;

namespace Arbitrage.Execution;

public enum PaperExecutionOrigin { Manual, AutomaticPaper }
public enum PaperSizingMode { FixedQuantity, LargestAdmissibleGridQuantity }
public enum PaperAutomationState { NotConfigured, Disarmed, Arming, Armed, Stopping, Faulted, KillSwitchLatched }
public enum PaperAutomationReason { None, Committed, DuplicateSuppressed, NotConfigured, RiskNotConfigured, ConfirmationRequired,
    AlreadyArmed, AutomationMustBeDisarmed, RevisionConflict, MonitoringStopped, NotPaperMode, GenerationUnavailable, PaperGenerationChanged,
    RiskPolicyChanged, AutomationPolicyChanged, KillSwitchLatched, Disarmed, BackendStopped, IntegrityFailure, BlockedByRisk,
    OpportunityUnavailable, InputQualityRejected, InsufficientDepth, MinimumEdgeRejected, MinimumProfitRejected, RiskRejected,
    SessionExecutionLimitReached, HourlyExecutionLimitReached, RelationshipSessionLimit, AutomationSessionDebitLimit, CooldownRejected,
    CommitConflict, ArithmeticOverflow, WorkerFault, InvalidProfile, NoAdmissibleAdaptiveQuantity, SizingBudgetDeferred }
public sealed record PaperAutomationSettings(PaperSizingMode SizingMode, decimal FixedQuantity, decimal MinimumFeeAdjustedEdgePerShare,
    decimal MinimumFeeAdjustedProfit, int MaximumExecutionsPerSession, int MaximumExecutionsPerHour,
    int MaximumExecutionsPerRelationshipPerSession, int MinimumSecondsBetweenExecutions, int RelationshipCooldownSeconds,
    decimal MaximumSessionDebitFractionPerBucket, int MaximumCandidatesPerCycle, bool RequireRealtime, bool AllowPolymarketBestEffort,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? MinimumQuantity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? MaximumQuantity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? QuantityStep = null)
{
    public bool Valid(PaperRiskProfile? risk) => risk is { IsValid: true } &&
        (SizingMode == PaperSizingMode.FixedQuantity ? FixedQuantity > 0 && FixedQuantity <= PaperPlanner.MaximumQuantity && FixedQuantity <= risk.Limits.MaximumRequestedQuantity &&
            MinimumQuantity is null && MaximumQuantity is null && QuantityStep is null :
            SizingMode == PaperSizingMode.LargestAdmissibleGridQuantity && PaperQuantityGrid.TryCreate(this, out _) && MaximumQuantity <= risk.Limits.MaximumRequestedQuantity) &&
        MinimumFeeAdjustedEdgePerShare >= risk.Limits.MinimumFeeAdjustedEdgePerShare && MinimumFeeAdjustedEdgePerShare < 1 &&
        MinimumFeeAdjustedProfit >= risk.Limits.MinimumFeeAdjustedProfit &&
        MaximumExecutionsPerSession is >= 1 and <= 1000 && MaximumExecutionsPerHour is >= 1 and <= 1000 &&
        MaximumExecutionsPerRelationshipPerSession >= 1 && MaximumExecutionsPerRelationshipPerSession <= MaximumExecutionsPerSession &&
        MinimumSecondsBetweenExecutions is >= 1 and <= 86400 && RelationshipCooldownSeconds is >= 1 and <= 86400 &&
        MaximumSessionDebitFractionPerBucket is > 0 and <= 1 && MaximumCandidatesPerCycle is >= 1 and <= 100 && RequireRealtime;
}
public sealed record PaperAutomationProfile(int PolicyVersion, Guid Revision, PaperAutomationSettings Settings, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, Guid UpdatedBy, string PolicyFingerprint)
{
    public const int Version = 2;
    public bool SupportedVersion => PolicyVersion == 1 && Settings.SizingMode == PaperSizingMode.FixedQuantity || PolicyVersion == Version;
    public bool Valid(PaperRiskProfile? risk) => Settings is not null && SupportedVersion && Revision != Guid.Empty && UpdatedBy != Guid.Empty &&
        CreatedAt <= UpdatedAt && Settings is not null && Settings.Valid(risk) && PolicyFingerprint == PaperAutomationPolicy.Hash(Settings);
}
public sealed record PaperAutomationPermit(Guid SessionId, Guid WorkspaceId, Guid ActorId, Guid GenerationId,
    PaperAutomationProfile Profile, Guid RiskRevision, string RiskFingerprint, Guid? KillRevision, DateTimeOffset StartedAt);
public sealed record PaperAutomationProof(Guid SessionId, int ProfileVersion, Guid ProfileRevision, string ProfileFingerprint,
    string TriggerInputStamp, Guid RelationshipId, DateTimeOffset TriggeredAt, PaperAutomationSettings Settings, PaperSizingProof? Sizing = null);
public sealed record PaperAutomationHistory(int SessionExecutions, int HourlyExecutions, int RelationshipSessionExecutions,
    DateTimeOffset? LastOpportunityAt, DateTimeOffset? LastRelationshipAt, bool DuplicateInput, ImmutableArray<PaperDebit> SessionDebits);

public static class PaperAutomationPolicy
{
    private sealed class DecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDecimal();
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) => writer.WriteRawValue(value.ToString("G29", CultureInfo.InvariantCulture));
    }
    private static readonly JsonSerializerOptions Canonical = new() { Converters = { new DecimalConverter() } };
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Canonical)));
    public static string Stamp(ArbitrageOpportunitySnapshot s, PaperAutomationProfile p) => p.PolicyVersion == 1 ? LegacyStamp(s, p) : Hash(new
    { Version = 2, Inputs = LegacyStamp(s, p), p.PolicyFingerprint, p.Settings.SizingMode, p.Settings.MinimumQuantity, p.Settings.MaximumQuantity, p.Settings.QuantityStep });
    private static string LegacyStamp(ArbitrageOpportunitySnapshot s, PaperAutomationProfile p) => Hash(new
    {
        s.OpportunityKey, s.RelationshipId, s.RelationshipRevision, s.RelationshipPolicyVersion, s.SourceFingerprint, s.TargetFingerprint,
        Books = s.Legs.Select(l => new { l.Instrument, l.SnapshotVersion, l.SourceMode, l.Continuity }).ToArray(),
        Fees = s.Fees?.Breakdown.Select(q => q.ScheduleFingerprint).Distinct().Order(StringComparer.Ordinal).ToArray(),
        FeeProfileRevision = s.Fees?.ProfileRevision, AutomationProfileRevision = p.Revision, p.Settings.FixedQuantity
    });
    public static Guid RequestId(Guid session, string key, string stamp, decimal quantity) => new(Convert.FromHexString(Hash(new { session, key, stamp, quantity }))[..16]);
    public static Guid RequestId(Guid session, string key, string stamp, decimal quantity, PaperSizingProof? sizing) => sizing is null ? RequestId(session, key, stamp, quantity) :
        new(Convert.FromHexString(Hash(new { Version = 2, session, key, stamp, quantity, sizing.Mode, sizing.DecisionFingerprint }))[..16]);
    public static PaperAutomationReason Quality(ArbitrageOpportunitySnapshot s, PaperAutomationSettings p)
    {
        if (s.RelationshipTrust != RelationshipTrust.Deterministic || !s.RelationshipEligible || !s.BooksActionable || !s.SkewAcceptable ||
            s.Status != OpportunityStatus.Detected || s.Fees?.State != FeeOpportunityStatus.FeeAdjustedDetected ||
            s.Strategy is not (OpportunityStrategy.CrossMarketBuyBothComplements or OpportunityStrategy.SingleMarketBinaryComplement) ||
            s.Legs.Length != 2 || s.Legs.Any(l => l.Action != DepthAction.Buy)) return PaperAutomationReason.OpportunityUnavailable;
        if (s.Legs.Any(l => l.SourceMode != BookSourceMode.Realtime ||
            !(l.Instrument.Exchange == "Kalshi" && l.Continuity == BookContinuity.Continuous ||
              l.Instrument.Exchange == "Polymarket" && p.AllowPolymarketBestEffort && l.Continuity == BookContinuity.BestEffort)))
            return PaperAutomationReason.InputQualityRejected;
        if (s.PairedQuantity < (p.SizingMode == PaperSizingMode.FixedQuantity ? p.FixedQuantity : p.MinimumQuantity)) return PaperAutomationReason.InsufficientDepth;
        return PaperAutomationReason.None;
    }
    public static PaperAutomationReason Evaluate(PaperAutomationSettings p, PaperPlan plan, PaperAutomationHistory history,
        IReadOnlyList<PaperRiskBucket> buckets, DateTimeOffset now)
    {
        var quality = Quality(plan.Proof, p); if (quality != PaperAutomationReason.None) return quality;
        if (p.SizingMode == PaperSizingMode.FixedQuantity ? plan.Quantity != p.FixedQuantity : !PaperQuantityGrid.Contains(p, plan.Quantity)) return PaperAutomationReason.InsufficientDepth;
        if (history.DuplicateInput) return PaperAutomationReason.DuplicateSuppressed;
        if (history.SessionExecutions >= p.MaximumExecutionsPerSession) return PaperAutomationReason.SessionExecutionLimitReached;
        if (history.HourlyExecutions >= p.MaximumExecutionsPerHour) return PaperAutomationReason.HourlyExecutionLimitReached;
        if (history.RelationshipSessionExecutions >= p.MaximumExecutionsPerRelationshipPerSession) return PaperAutomationReason.RelationshipSessionLimit;
        if (history.LastOpportunityAt is { } last && now - last < TimeSpan.FromSeconds(p.MinimumSecondsBetweenExecutions) ||
            history.LastRelationshipAt is { } related && now - related < TimeSpan.FromSeconds(p.RelationshipCooldownSeconds)) return PaperAutomationReason.CooldownRejected;
        if (plan.Proof.Fees?.FeeAdjustedEdgePerShare is not { } edge || edge < p.MinimumFeeAdjustedEdgePerShare) return PaperAutomationReason.MinimumEdgeRejected;
        if (plan.Proof.Fees?.FeeAdjustedGuaranteedProfit is not { } profit || profit < p.MinimumFeeAdjustedProfit) return PaperAutomationReason.MinimumProfitRejected;
        try
        {
            foreach (var d in plan.Debits)
            {
                var b = buckets.SingleOrDefault(b => b.Exchange == d.Exchange && b.Currency == d.Currency);
                if (b is null) return PaperAutomationReason.IntegrityFailure;
                var used = history.SessionDebits.Where(x => x.Exchange == d.Exchange && x.Currency == d.Currency).Sum(x => x.Total);
                if (checked(used + d.Total) > checked(b.InitialCash * p.MaximumSessionDebitFractionPerBucket)) return PaperAutomationReason.AutomationSessionDebitLimit;
            }
            return PaperAutomationReason.None;
        }
        catch (OverflowException) { return PaperAutomationReason.ArithmeticOverflow; }
    }
}
