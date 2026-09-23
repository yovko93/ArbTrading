using System.Net;
using System.Text.Json;
using Arbitrage.Connectors;
using Arbitrage.Domain;

namespace Arbitrage.Application.Tests;

public sealed class PublicFeeSourceTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Null(request.Headers.Authorization); Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Contains(request.RequestUri.Host, new[] { "gamma-api.polymarket.com", "external-api.kalshi.com" }); Requests.Add(request.RequestUri);
            return Task.FromResult(response(request));
        }
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    [Fact] public async Task Polymarket_uses_market_specific_parameters_not_category_and_excludes_rebates()
    {
        using var handler = new Handler(_ => Json("""{"id":"123","category":"Crypto","clobTokenIds":"[\"456\",\"789\"]","feesEnabled":true,"feeSchedule":{"rate":0.04,"exponent":1,"takerOnly":true,"rebateRate":0.9}}"""));
        var schedule = await new PublicFeeSource(new HttpClient(handler), TimeProvider.System).ReadAsync("Polymarket", "123", default);
        Assert.Equal(.04m, Assert.Single(schedule.Rules).Rate); Assert.Null(schedule.VerificationIssue); Assert.Single(handler.Requests);
        decimal accumulator = 0;
        var q = FeeMath.Quote(FeeScheduleResolver.Resolve(schedule, DateTimeOffset.UtcNow), new("Polymarket", "123", "456", LiquidityRole.Taker, 100, .5m, DepthAction.Buy, KalshiFeeAccountProfile.Unknown), ref accumulator);
        Assert.Equal(1m, q.TotalFee); Assert.Equal("NotIncluded", q.ProgramRebates);
    }
    [Theory] [InlineData("{}", FeeStatus.ScheduleUnavailable)]
    [InlineData("{\"feesEnabled\":true}", FeeStatus.ScheduleUnavailable)]
    [InlineData("{\"feesEnabled\":false}", FeeStatus.KnownExact)]
    public void Missing_fee_parameters_are_not_free(string fields, FeeStatus expected)
    {
        var json = "{\"id\":\"1\",\"clobTokenIds\":[\"2\"]" + (fields == "{}" ? "" : "," + fields[1..^1]) + "}";
        using var doc = JsonDocument.Parse(json); var now = DateTimeOffset.UtcNow;
        Assert.Equal(expected, FeeScheduleResolver.Resolve(PublicFeeSource.ParsePolymarket(doc.RootElement, "1", now, "fixture"), now).Status);
    }
    [Fact] public async Task Kalshi_reads_all_hierarchy_sources_and_clear_semantics_but_blocks_regulatory_conflict()
    {
        using var handler = new Handler(r => Json(r.RequestUri!.AbsolutePath switch
        {
            "/trade-api/v2/markets/M" => """{"market":{"ticker":"M","event_ticker":"E"}}""",
            "/trade-api/v2/events/E" => """{"event":{"event_ticker":"E","series_ticker":"S","fee_type_override":null,"fee_multiplier_override":null}}""",
            "/trade-api/v2/series/S" => """{"series":{"ticker":"S","fee_type":"quadratic","fee_multiplier":1}}""",
            "/trade-api/v2/series/fee_changes" => """{"series_fee_change_arr":[{"id":"1","series_ticker":"S","fee_type":"quadratic","fee_multiplier":0.5,"scheduled_ts":"2099-01-01T00:00:00Z"}]}""",
            "/trade-api/v2/events/fee_changes" => """{"event_fee_changes":[{"id":"2","event_ticker":"E","series_ticker":"S","fee_type_override":null,"fee_multiplier_override":null,"scheduled_ts":"2099-02-01T00:00:00Z"}],"cursor":""}""",
            _ => throw new InvalidOperationException()
        }));
        var schedule = await new PublicFeeSource(new HttpClient(handler), TimeProvider.System).ReadAsync("Kalshi", "M", default);
        Assert.Equal(5, handler.Requests.Count); Assert.Equal(4, schedule.Rules.Length); Assert.Equal(2, schedule.Rules.Count(r => r.Clear));
        Assert.Equal(FeeStatus.InvalidFeeMetadata, FeeScheduleResolver.Resolve(schedule, DateTimeOffset.UtcNow).Status);
    }
    [Theory] [InlineData(302)] [InlineData(401)] [InlineData(403)] [InlineData(429)]
    public async Task Denial_redirect_and_long_retry_after_never_forward_credentials_or_bypass(int code)
    {
        using var handler = new Handler(_ => { var response = new HttpResponseMessage((HttpStatusCode)code); response.Headers.RetryAfter = new(TimeSpan.FromHours(1)); return response; });
        await Assert.ThrowsAsync<HttpRequestException>(() => new PublicFeeSource(new HttpClient(handler), TimeProvider.System).ReadAsync("Polymarket", "1", default));
        Assert.Single(handler.Requests);
    }
    [Fact] public async Task Retry_is_bounded_and_response_size_and_identity_are_checked()
    {
        var count = 0; using var handler = new Handler(_ => { count++; var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); response.Headers.RetryAfter = new(TimeSpan.Zero); return response; });
        await Assert.ThrowsAsync<HttpRequestException>(() => new PublicFeeSource(new HttpClient(handler), TimeProvider.System).ReadAsync("Polymarket", "1", default)); Assert.Equal(3, count);
        using var wrong = new Handler(_ => Json("""{"id":"wrong"}"""));
        await Assert.ThrowsAsync<JsonException>(() => new PublicFeeSource(new HttpClient(wrong), TimeProvider.System).ReadAsync("Polymarket", "1", default));
        using var huge = new Handler(_ => Json(new string(' ', 1_000_001)));
        await Assert.ThrowsAsync<JsonException>(() => new PublicFeeSource(new HttpClient(huge), TimeProvider.System).ReadAsync("Polymarket", "1", default));
    }
}
