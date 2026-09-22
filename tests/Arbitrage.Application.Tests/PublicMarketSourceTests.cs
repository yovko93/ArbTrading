using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Arbitrage.Application;
using Arbitrage.Connectors;

namespace Arbitrage.Application.Tests;

public sealed class PublicMarketSourceTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Null(request.Headers.Authorization);
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static string Market(int id) =>
        $$"""{"id":"{{id}}","question":"Question {{id}}","active":true,"closed":false,"outcomes":"[\"No\",\"Yes\"]","clobTokenIds":"[\"999999999999999999999999999999\",\"2\"]"}""";

    [Fact]
    public async Task Polymarket_keyset_continues_beyond_one_thousand_and_preserves_raw_outcome_order()
    {
        var handler = new Handler(request =>
        {
            var query = request.RequestUri!.Query;
            Assert.Contains("closed=false", query); Assert.Contains("limit=400", query);
            var page = query.Contains("after_cursor=opaque-2") ? 2 : query.Contains("after_cursor=opaque-1") ? 1 : 0;
            var count = page == 2 ? 405 : 400;
            var items = string.Join(",", Enumerable.Range(page * 400, count).Select(Market));
            return Json("{\"markets\":[" + items + "],\"next_cursor\":" +
                (page == 2 ? "null" : $"\"opaque-{page + 1}\"") + "}");
        });
        using var http = new HttpClient(handler);
        var source = new PolymarketMarketSource(http);
        string? cursor = null; var received = new List<DiscoveredMarket>();
        do
        {
            var page = await source.ReadPageAsync("nonfinalized", cursor, 400, DateTimeOffset.UtcNow.AddMinutes(1), default);
            received.AddRange(page.Markets); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(1205, received.Select(m => m.NativeId).Distinct().Count());
        Assert.All(handler.Requests, uri => Assert.Equal("gamma-api.polymarket.com", uri.Host));
        Assert.Equal("No", received[0].Outcomes[0].Label);
        Assert.Equal("999999999999999999999999999999", received[0].Outcomes[0].NativeTokenId);
    }

    [Theory]
    [InlineData("unopened")]
    [InlineData("open")]
    [InlineData("paused")]
    public async Task Kalshi_scopes_keep_filter_and_opaque_cursor_on_short_pages(string scope)
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("external-api.kalshi.com", request.RequestUri!.Host);
            Assert.Contains("status=" + scope, request.RequestUri.Query);
            return Json(request.RequestUri.Query.Contains("cursor=next%2Bpage")
                ? "{\"markets\":[{\"ticker\":\"K-2\",\"status\":\"paused\"}],\"cursor\":\"\"}"
                : "{\"markets\":[{\"ticker\":\"K-1\",\"status\":\"open\",\"mve_collection_ticker\":\"MVE\"}],\"cursor\":\"next+page\"}");
        });
        using var http = new HttpClient(handler);
        var source = new KalshiMarketSource(http);
        var first = await source.ReadPageAsync(scope, null, 100, DateTimeOffset.UtcNow.AddMinutes(1), default);
        var second = await source.ReadPageAsync(scope, first.NextCursor, 100, DateTimeOffset.UtcNow.AddMinutes(1), default);
        Assert.Single(first.Markets); Assert.Equal("Multivariate", first.Markets[0].Classification);
        Assert.Equal("next+page", first.NextCursor); Assert.Null(second.NextCursor);
        Assert.Equal("Paused", second.Markets[0].Status);
        Assert.Null(second.Markets[0].Outcomes[0].NativeTokenId);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Malformed_record_is_counted_but_envelope_failure_is_not_empty_success()
    {
        var call = 0;
        using var http = new HttpClient(new Handler(_ => Json(++call == 1
            ? "{\"markets\":[{}, {\"id\":\"1\",\"outcomes\":\"[\\\"Yes\\\",\\\"No\\\"]\",\"clobTokenIds\":\"[\\\"only-one\\\"]\"}],\"next_cursor\":null}"
            : "{\"unrelated\":[]}")));
        var source = new PolymarketMarketSource(http);
        var page = await source.ReadPageAsync("nonfinalized", null, 20, DateTimeOffset.UtcNow.AddMinutes(1), default);
        Assert.Equal(1, page.MalformedRecords); Assert.Single(page.Markets);
        Assert.Empty(page.Markets[0].Outcomes); Assert.Contains(page.Markets[0].Warnings, w => w.Contains("align"));
        var failure = await Assert.ThrowsAsync<MarketDiscoveryException>(() =>
            source.ReadPageAsync("nonfinalized", null, 20, DateTimeOffset.UtcNow.AddMinutes(1), default));
        Assert.Equal("InvalidEnvelope", failure.Code);
    }

    [Fact]
    public async Task Rate_limit_waiting_beyond_budget_is_visible_and_access_denial_is_not_retried()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        }));
        var source = new KalshiMarketSource(http);
        var limited = await Assert.ThrowsAsync<MarketDiscoveryException>(() =>
            source.ReadPageAsync("open", null, 20, DateTimeOffset.UtcNow.AddSeconds(5), default));
        Assert.Equal("RateLimited", limited.Code); Assert.Equal(1, calls);
        using var deniedHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.Forbidden)));
        var denied = await Assert.ThrowsAsync<MarketDiscoveryException>(() =>
            new KalshiMarketSource(deniedHttp).ReadPageAsync("open", null, 20, DateTimeOffset.UtcNow.AddSeconds(5), default));
        Assert.Equal("PublicAccessDenied", denied.Code);
    }

    [Fact]
    public async Task Transient_get_failure_retries_and_invalid_json_fails_explicitly()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            if (++calls == 1) throw new HttpRequestException("temporary");
            return Json("{\"markets\":[],\"cursor\":\"\"}");
        }));
        var page = await new KalshiMarketSource(http).ReadPageAsync("open", null, 20,
            DateTimeOffset.UtcNow.AddSeconds(10), default);
        Assert.Empty(page.Markets); Assert.Null(page.NextCursor); Assert.Equal(2, calls);
        using var invalidHttp = new HttpClient(new Handler(_ => Json("not-json")));
        var invalid = await Assert.ThrowsAsync<MarketDiscoveryException>(() =>
            new KalshiMarketSource(invalidHttp).ReadPageAsync("open", null, 20,
                DateTimeOffset.UtcNow.AddSeconds(10), default));
        Assert.Equal("InvalidJson", invalid.Code);
    }

    [Fact]
    public async Task Cancellation_stops_a_waiting_public_request()
    {
        using var source = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new AsyncHandler(async (_, ct) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json("{\"markets\":[]}");
        }));
        var pending = new PolymarketMarketSource(http).ReadPageAsync("nonfinalized", null, 20,
            DateTimeOffset.UtcNow.AddSeconds(10), source.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
}
