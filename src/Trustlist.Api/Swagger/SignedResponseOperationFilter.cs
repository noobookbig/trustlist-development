using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Trustlist.Api.Swagger;

/// <summary>
/// MAS-727 — make Swagger reflect reality on the signed directory surface.
///
/// The controllers under <c>/v1/issuers</c>, <c>/v1/verifiers</c> and
/// <c>/v1/wallet-providers</c> return plain JSON via <c>Ok(...)</c>; the
/// <c>JwsResponseMiddleware</c> (MAS-696) then rewrites a successful 2xx body to a
/// JWS-compact token with <c>Content-Type: application/jwt</c>. Swagger only sees
/// the controller's declared output, so the UI showed these endpoints as if they
/// returned unsigned <c>application/json</c> — which is why "swagger looks unsigned".
///
/// This is a DOCUMENTATION-ONLY filter. It does NOT add an MVC output formatter
/// (doing that via <c>[Produces("application/jwt")]</c> breaks content negotiation
/// with a 406 because nothing can format the controller object as application/jwt).
/// It only relabels the documented 2xx response media type to <c>application/jwt</c>
/// and adds a description so API consumers know the body is a signed JWS.
/// </summary>
public sealed class SignedResponseOperationFilter : IOperationFilter
{
    private static readonly string[] SignedPathPrefixes =
    {
        "/v1/issuers",
        "/v1/verifiers",
        "/v1/wallet-providers",
    };

    private const string JwsDescription =
        "Signed response: a JWS-compact token (EdDSA/Ed25519) — " +
        "`base64url(header).base64url(payload).base64url(signature)`. " +
        "Verify with the publisher key at `/.well-known/trustlist-jwks.json`. " +
        "The payload is the snake_case JSON record shown below.";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = "/" + (context.ApiDescription.RelativePath ?? string.Empty);
        var isSigned = SignedPathPrefixes.Any(p =>
            path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (!isSigned) return;

        foreach (var (statusCode, response) in operation.Responses)
        {
            // Only successful responses are signed by the middleware (2xx).
            if (!statusCode.StartsWith('2')) continue;

            // Preserve the JSON schema (the decoded payload shape) but advertise the
            // signed media type so the UI no longer implies a plain JSON body.
            OpenApiSchema? schema = null;
            if (response.Content.TryGetValue("application/json", out var json))
                schema = json.Schema;

            response.Content.Clear();
            response.Content["application/jwt"] = new OpenApiMediaType { Schema = schema };

            response.Description = string.IsNullOrWhiteSpace(response.Description)
                ? JwsDescription
                : response.Description + " " + JwsDescription;
        }
    }
}
