using Microsoft.IdentityModel.Tokens;

namespace Trustlist.Api.Signing;

/// <summary>
/// Trustlist publisher signing configuration. Binds from the <c>TrustlistPublisher</c>
/// configuration section (env-var: <c>TrustlistPublisher__*</c>).
///
/// The TL publisher is the trust-list authority that signs the role-keyed directory
/// responses. v0 spec §4 mandates EdDSA / Ed25519 and JWS-compact serialization, so
/// the only supported algorithm is <see cref="SecurityAlgorithms.EdDsa"/>. This is
/// a one-way-door choice — changing the algorithm after issuance is a key rollover,
/// not a code change.
///
/// Key custody is intentionally scoped to a single Ed25519 seed encoded as base64
/// (32 bytes). On the local dev cut the seed is provisioned via
/// <c>TRUSTLIST_PUBLISHER_PRIVATE_KEY</c> in <c>.env</c> (gitignored, like
/// <c>JWT_SIGNING_KEY</c>). In a real deployment the TL Authority would source the
/// seed from a KMS / HSM and never have it on disk; the same field would be wired
/// to that KMS by the deploy story.
///
/// The <see cref="Kid"/> is the well-known identifier clients use to look the
/// public key up in the JWKS document at <c>/.well-known/trustlist-jwks.json</c>.
/// On rotation, publish the new <see cref="Kid"/> alongside the old one and let
/// the JWKS-TTL window (24h, per the v0 spec caching rule) expire before
/// retiring the old key. See the README "Signed Responses" section for the full
/// rotation procedure.
/// </summary>
public class PublisherSigningOptions
{
    public const string SectionName = "TrustlistPublisher";

    /// <summary>
    /// Base64-encoded 32-byte Ed25519 private key seed. Generate with:
    ///   openssl rand -base64 32
    /// </summary>
    public string PrivateKeySeedBase64 { get; set; } = string.Empty;

    /// <summary>
    /// Key id (kid) advertised in the JWS <c>protected</c> header and looked up
    /// in the JWKS. Stable across rotations of the underlying seed.
    /// </summary>
    public string Kid { get; set; } = string.Empty;
}