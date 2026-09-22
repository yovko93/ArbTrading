using System.Net;
using System.Text;
using Arbitrage.Connectors;
using Arbitrage.Application;
using Arbitrage.Domain;

namespace Arbitrage.Application.Tests;
public sealed class RelationshipMetadataTests
{
    private sealed class Handler(Func<Uri, string> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Headers.Authorization);
            Assert.Equal("https", request.RequestUri!.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request.RequestUri), Encoding.UTF8, "application/json") });
        }
    }
    [Fact]
    public async Task Kalshi_enrichment_is_three_bounded_public_reads_and_keeps_rule_source_metadata()
    {
        var handler = new Handler(uri => uri.AbsolutePath.Contains("/markets/", StringComparison.Ordinal)
            ? """{"market":{"ticker":"K","event_ticker":"E","title":"Question","rules_primary":"Primary","rules_secondary":"Secondary","floor_strike":50,"early_close_condition":"Occurrence"}}"""
            : uri.AbsolutePath.Contains("/events/", StringComparison.Ordinal) ? """{"event":{"event_ticker":"E","series_ticker":"S","mutually_exclusive":true}}"""
            : """{"series":{"settlement_sources":[{"name":"Authority","url":"https://example.invalid/rules"}],"contract_terms_url":"https://example.invalid/terms"}}""");
        var metadata = await new RelationshipMetadataSource(new HttpClient(handler)).ReadAsync("Kalshi", "K", null, default);
        Assert.Equal(3, handler.Calls); Assert.Equal("Primary\nSecondary", metadata.Rules); Assert.Contains("Authority", metadata.ResolutionSource!);
        Assert.Contains("mutually_exclusive", metadata.SemanticMetadataJson); Assert.Contains("contract_terms_url", metadata.SemanticMetadataJson);
    }
    [Fact]
    public async Task Polymarket_description_is_preserved_as_rules_without_inventing_set_proof()
    {
        var handler = new Handler(_ => """{"id":"P","question":"Question","description":"Settlement conditions","resolutionSource":"Official source","negRisk":true,"outcomes":"[\"Yes\",\"No\"]","clobTokenIds":"[\"a\",\"b\"]"}""");
        var metadata = await new RelationshipMetadataSource(new HttpClient(handler)).ReadAsync("Polymarket", "P", null, default);
        Assert.Equal(1, handler.Calls); Assert.Equal("Settlement conditions", metadata.Rules); Assert.Contains("negRisk", metadata.SemanticMetadataJson);
    }
    [Fact]
    public async Task Mismatched_market_identity_is_rejected()
    {
        var handler = new Handler(_ => """{"id":"wrong"}""");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => new RelationshipMetadataSource(new HttpClient(handler)).ReadAsync("Polymarket", "P", null, default));
    }
    [Fact]
    public void Title_hints_preserve_meaningful_differences_without_claiming_authority()
    {
        var election = RelationshipHints.FromTitle("Will X win the US election in 2028 with >50 USD?");
        var nominee = RelationshipHints.FromTitle("Will X be California nominee in 2032 with >=50 EUR?");
        Assert.Equal("win election", election.Predicate!.Value); Assert.Equal("nomination", nominee.Predicate!.Value);
        Assert.Equal(FactSource.Title, election.Predicate.Source); Assert.NotEqual(election.Edition!.Value, nominee.Edition!.Value);
        Assert.NotEqual(election.Threshold!.Value, nominee.Threshold!.Value); Assert.Null(election.Subject); Assert.Null(election.Window);
    }
}
