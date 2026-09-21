using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Arbitrage.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Arbitrage.Backend;

public sealed class LocalCredential
{
    public string Value { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public bool Matches(string candidate) => candidate.Length == 64 &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Value), Encoding.ASCII.GetBytes(candidate));
}

public sealed class LocalAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, LocalCredential credential, ILocalProfileStore profiles)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LocalClient";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var headers)) return AuthenticateResult.NoResult();
        var header = headers.Count == 1 ? headers[0] : null;
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || !credential.Matches(header[7..]))
            return AuthenticateResult.Fail("Invalid local credential.");
        var profile = await profiles.GetAsync(Context.RequestAborted);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, profile.UserId.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

public sealed class RequestActor(IHttpContextAccessor accessor) : IRequestActor
{
    public Guid? UserId => accessor.HttpContext?.User is { Identity.IsAuthenticated: true } user &&
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
