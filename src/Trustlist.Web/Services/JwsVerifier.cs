using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Trustlist.Web.Services;

/// <summary>
/// MAS-696 — JWS-compact response verifier for consumers of the public
/// role-keyed directory surface (<c>/v1/issuers/...</c>,
/// <c>/v1/wallet-providers/...</c>, <c>/v1/verifiers/...</c>).
///
/// A holder-wallet, an issuer, or a verifier service can call
/// <see cref="FetchAndVerifyAsync"/> to download the JWKS document, fetch the
/// directory endpoint, and verify the JWS signature in one round-trip — this is
/// the smoke-test flow the MAS-696 acceptance criterion asks for in the Web
/// project.
///
/// Caching policy: the v0 spec mandates a 24h JWKS TTL. <see cref="JwksCache"/>
/// is a tiny in-memory cache that respects the spec; production code should
/// swap it for a persistent cache so a process restart does not invalidate
/// the entire trust chain.
///
/// Algorithm: EdDSA / Ed25519 only. The shape of the JWS
/// (<c>header.payload.signature</c>) is RFC 7515; the curve is RFC 8032.
/// </summary>
public class JwsVerifier
{
    private readonly HttpClient _http;
    private readonly JwksCache _cache;

    public JwsVerifier(HttpClient http, JwksCache cache)
    {
        _http = http;
        _cache = cache;
    }

    /// <summary>
    /// Resolve the publisher <c>kid</c> to verify against. Verifier-grade trust
    /// pins the kid out-of-band: callers SHOULD pass the operator-configured
    /// <paramref name="pinnedKid"/>. When no kid is pinned (v0 single-publisher
    /// deployments) we fall back to the sole key advertised in the JWKS — but we
    /// refuse to guess when the JWKS advertises more than one key, because then
    /// "trust the only key" is ambiguous and an attacker who can add a key could
    /// steer us to the wrong one.
    /// </summary>
    public async Task<(string? kid, string? error)> ResolveKidAsync(
        string? pinnedKid,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(pinnedKid)) return (pinnedKid, null);

        var jwks = await _cache.GetAsync(_http, ct);
        return jwks.Keys.Count switch
        {
            1 => (jwks.Keys.Keys.First(), null),
            0 => (null, "JWKS advertises no keys; cannot establish a trust anchor."),
            _ => (null,
                $"JWKS advertises {jwks.Keys.Count} keys and no kid was pinned; " +
                "refusing to guess. Configure TrustlistPublisher:Kid."),
        };
    }

    public async Task<JwsVerificationResult> FetchAndVerifyAsync(
        string directoryUrl,
        string expectedKid,
        CancellationToken ct = default)
    {
        // 1. JWKS — cache 24h per v0 spec §TL cache.
        var jwks = await _cache.GetAsync(_http, ct);
        if (!jwks.TryGetKey(expectedKid, out var jwk))
        {
            return JwsVerificationResult.Fail(
                $"JWKS does not advertise kid '{expectedKid}'.",
                JwsFailureReason.UnknownKid);
        }

        // 2. Directory response — must be application/jwt, JWS-compact.
        using var req = new HttpRequestMessage(HttpMethod.Get, directoryUrl);
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/jwt"));
        var resp = await _http.SendAsync(req, ct);
        if (resp.Content.Headers.ContentType?.MediaType != "application/jwt")
        {
            return JwsVerificationResult.Fail(
                $"Unexpected Content-Type: {resp.Content.Headers.ContentType?.MediaType ?? "<null>"}. " +
                "Signed directory responses must be served as application/jwt.",
                JwsFailureReason.WrongContentType);
        }
        var jws = await resp.Content.ReadAsStringAsync(ct);
        var parts = jws.Split('.');
        if (parts.Length != 3)
        {
            return JwsVerificationResult.Fail(
                $"JWS must have exactly 3 segments (header.payload.signature); got {parts.Length}.",
                JwsFailureReason.Malformed);
        }

        // 3. Header check — alg + kid + typ + cty.
        var header = ParseHeader(parts[0]);
        if (header.Alg != "EdDSA")
        {
            return JwsVerificationResult.Fail(
                $"alg='{header.Alg}' is not EdDSA. Alg confusion is the canonical JWT pitfall.",
                JwsFailureReason.AlgNotAllowed);
        }
        if (header.Kid != expectedKid)
        {
            return JwsVerificationResult.Fail(
                $"JWS kid='{header.Kid}' does not match expected kid='{expectedKid}'.",
                JwsFailureReason.UnknownKid);
        }

        // 4. Signature verification with the JWKS public key.
        var pub = new Ed25519PublicKeyParameters(Base64UrlDecode(jwk.X), 0);
        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = Base64UrlDecode(parts[2]);
        var verifier = new Ed25519Signer();
        verifier.Init(false, pub);
        verifier.BlockUpdate(signingInput, 0, signingInput.Length);
        if (!verifier.VerifySignature(signature))
        {
            return JwsVerificationResult.Fail(
                "Ed25519 signature verification failed — body was modified after signing.",
                JwsFailureReason.SignatureInvalid);
        }

        // 5. Decode the payload so callers can read the record.
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        return JwsVerificationResult.Ok(header, payloadJson);
    }

    private static JwsHeader ParseHeader(string protectedB64)
    {
        var headerJson = Encoding.UTF8.GetString(Base64UrlDecode(protectedB64));
        using var doc = JsonDocument.Parse(headerJson);
        var root = doc.RootElement;
        return new JwsHeader(
            Alg: root.GetProperty("alg").GetString() ?? string.Empty,
            Kid: root.GetProperty("kid").GetString() ?? string.Empty,
            Typ: root.TryGetProperty("typ", out var t) ? t.GetString() ?? string.Empty : string.Empty,
            Cty: root.TryGetProperty("cty", out var c) ? c.GetString() ?? string.Empty : string.Empty);
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}

public record JwsHeader(string Alg, string Kid, string Typ, string Cty);

public enum JwsFailureReason
{
    UnknownKid,
    AlgNotAllowed,
    SignatureInvalid,
    Malformed,
    WrongContentType,
}

public record JwsVerificationResult(
    bool IsValid,
    JwsHeader? Header,
    string? PayloadJson,
    string? FailureMessage,
    JwsFailureReason? FailureReason)
{
    public static JwsVerificationResult Ok(JwsHeader header, string payload) =>
        new(true, header, payload, null, null);

    public static JwsVerificationResult Fail(string message, JwsFailureReason reason) =>
        new(false, null, null, message, reason);
}

/// <summary>
/// Tiny in-memory JWKS cache that respects the v0 spec 24h TTL. Swap for a
/// persistent cache in production so a process restart does not require
/// re-fetching the trust anchor.
/// </summary>
public class JwksCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private JwksDocument? _doc;

    public async Task<JwksDocument> GetAsync(HttpClient http, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_doc is not null && DateTimeOffset.UtcNow - _loadedAt < Ttl) return _doc;
            var json = await http.GetStringAsync("/.well-known/trustlist-jwks.json", ct);
            _doc = JwksDocument.Parse(json);
            _loadedAt = DateTimeOffset.UtcNow;
            return _doc;
        }
        finally { _gate.Release(); }
    }
}

public record JwksDocument(IReadOnlyDictionary<string, JwkPublicKey> Keys)
{
    public bool TryGetKey(string kid, out JwkPublicKey jwk) =>
        Keys.TryGetValue(kid, out jwk!);

    public static JwksDocument Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var map = new Dictionary<string, JwkPublicKey>(StringComparer.Ordinal);
        foreach (var k in doc.RootElement.GetProperty("keys").EnumerateArray())
        {
            var kid = k.GetProperty("kid").GetString() ?? throw new InvalidOperationException("JWK missing kid");
            map[kid] = new JwkPublicKey(
                Kty: k.GetProperty("kty").GetString() ?? string.Empty,
                Crv: k.TryGetProperty("crv", out var crv) ? crv.GetString() ?? string.Empty : string.Empty,
                Kid: kid,
                X: k.GetProperty("x").GetString() ?? string.Empty,
                Alg: k.TryGetProperty("alg", out var alg) ? alg.GetString() ?? string.Empty : string.Empty);
        }
        return new JwksDocument(map);
    }
}

public record JwkPublicKey(string Kty, string Crv, string Kid, string X, string Alg);