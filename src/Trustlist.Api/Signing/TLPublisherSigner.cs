using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Trustlist.Api.Signing;

/// <summary>
/// Trustlist publisher signer — produces a JWS-compact
/// (<c>base64url(header).base64url(payload).base64url(signature)</c>) over the
/// snake_case JSON body of a public role-keyed directory response.
///
/// Format reference (per MAS-696 / v0 trustlist spec §4):
///   header    : { "alg": "EdDSA", "kid": "&lt;kid&gt;", "typ": "trustlist-role-record+jwt", "cty": "application/json" }
///   payload   : the exact snake_case JSON bytes the controller returned before signing
///   signature : Ed25519 over ASCII(BASE64URL(header) + "." + BASE64URL(payload))
///
/// Crypto choice: BouncyCastle <see cref="Ed25519Signer"/> + 32-byte seed import.
/// The MAS-696 task description suggested <c>Microsoft.IdentityModel.Tokens.EdDsaSecurityKey</c>
/// with <c>SecurityAlgorithms.EdDsa</c>; that API does not exist in IdentityModel
/// 7.x (the version JwtBearer 8.0.8 ships) nor in IdentityModel 8.x at the time of
/// writing. .NET 8 BCL also does not expose <c>System.Security.Cryptography.Ed25519</c>
/// (added in .NET 9). BouncyCastle is the smallest dependency that gives us a
/// well-audited, deterministic RFC 8032 Ed25519 signer in .NET 8 and matches the
/// cryptography spec the v0 trustlist doc relies on.
/// </summary>
public class TLPublisherSigner
{
    public const string JwtType = "trustlist-role-record+jwt";
    public const string ContentType = "application/json";
    public const string AlgorithmName = "EdDSA";
    public const string CurveName = "Ed25519";
    public const string KeyTypeOkp = "OKP";

    private readonly PublisherSigningOptions _options;
    private readonly Ed25519PrivateKeyParameters _privateKey;
    private readonly Ed25519PublicKeyParameters _publicKey;
    private readonly byte[] _rawPublicKey;

    public TLPublisherSigner(PublisherSigningOptions options)
    {
        _options = options;
        var seed = DecodeSeed(options.PrivateKeySeedBase64);
        _privateKey = new Ed25519PrivateKeyParameters(seed, 0);
        _publicKey = _privateKey.GeneratePublicKey();
        _rawPublicKey = _publicKey.GetEncoded(); // 32 bytes raw Ed25519 public key
    }

    /// <summary>
    /// The <c>kid</c> advertised in every JWS <c>protected</c> header and in the
    /// JWKS document.
    /// </summary>
    public string Kid => _options.Kid;

    /// <summary>
    /// RFC 7517 JWK shape for the publisher's Ed25519 public key. Published at
    /// <c>/.well-known/trustlist-jwks.json</c>:
    ///   <c>{ kty: "OKP", crv: "Ed25519", kid: "&lt;kid&gt;", x: "&lt;base64url(32-byte pubkey)&gt;", alg: "EdDSA", use: "sig" }</c>
    /// </summary>
    public Jwk PublicJwk => new(
        Kty: KeyTypeOkp,
        Crv: CurveName,
        Kid: _options.Kid,
        X: Base64UrlEncode(_rawPublicKey),
        Alg: AlgorithmName,
        Use: "sig");

    /// <summary>
    /// Produce a JWS-compact over the supplied UTF-8 JSON payload. The payload is
    /// the exact snake_case bytes the controller wrote, so signing-then-
    /// deserialising yields the same record (Test 1 acceptance gate).
    /// </summary>
    public string Sign(ReadOnlySpan<byte> jsonPayloadUtf8)
    {
        var headerJson = $$"""
{"alg":"{{AlgorithmName}}","kid":"{{_options.Kid}}","typ":"{{JwtType}}","cty":"{{ContentType}}"}
""";
        var protectedB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadB64 = Base64UrlEncode(jsonPayloadUtf8);
        var signingInput = Encoding.ASCII.GetBytes($"{protectedB64}.{payloadB64}");

        var signer = new Ed25519Signer();
        signer.Init(true, _privateKey);
        signer.BlockUpdate(signingInput, 0, signingInput.Length);
        var signature = signer.GenerateSignature();
        var signatureB64 = Base64UrlEncode(signature);

        return $"{protectedB64}.{payloadB64}.{signatureB64}";
    }

    private static byte[] DecodeSeed(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException(
                "TrustlistPublisher:PrivateKeySeedBase64 is missing. " +
                "Set TRUSTLIST_PUBLISHER_PRIVATE_KEY (32-byte base64-encoded Ed25519 seed) in .env. " +
                "Generate with: openssl rand -base64 32");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "TrustlistPublisher:PrivateKeySeedBase64 is not valid base64.", ex);
        }
        if (bytes.Length != 32)
            throw new InvalidOperationException(
                $"TrustlistPublisher:PrivateKeySeedBase64 must decode to exactly 32 bytes (Ed25519 seed). Got {bytes.Length}.");
        return bytes;
    }

    internal static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        var s = Convert.ToBase64String(bytes);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>
/// RFC 7517 JSON Web Key shape for the publisher's Ed25519 public key. Local DTO
/// so the signing layer does not depend on IdentityModel's <c>JsonWebKey</c>
/// (which has IdentityModel-specific defaults we don't need here).
/// </summary>
public record Jwk(
    string Kty,
    string Crv,
    string Kid,
    string X,
    string Alg,
    string Use);