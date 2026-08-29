using Microsoft.IdentityModel.Tokens;

namespace Pat.Aca.LinuxLab.Api.Services;

/// <summary>
/// Fetches and caches Cloudflare Access's signing keys directly from its
/// certs endpoint. Needed because that endpoint returns a bare JWKS
/// document, not a full OIDC discovery document with a jwks_uri pointer —
/// JwtBearerOptions.MetadataAddress's default retriever expects the
/// latter and silently ends up with zero signing keys against Cloudflare's
/// actual response shape. Confirmed via a real "No security keys were
/// provided to validate the signature" 401, not assumed.
/// </summary>
public sealed class CloudflareJwksProvider(HttpClient http)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ICollection<SecurityKey>? _keys;
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    public ICollection<SecurityKey> GetSigningKeys(string certsUrl)
    {
        if (IsFresh()) return _keys!;

        _lock.Wait();
        try
        {
            if (IsFresh()) return _keys!;

            var json = http.GetStringAsync(certsUrl).GetAwaiter().GetResult();
            var jwks = new JsonWebKeySet(json);
            _keys = jwks.Keys.Cast<SecurityKey>().ToList();
            _fetchedAt = DateTimeOffset.UtcNow;
            return _keys;
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool IsFresh() => _keys is not null && DateTimeOffset.UtcNow - _fetchedAt < CacheDuration;
}
