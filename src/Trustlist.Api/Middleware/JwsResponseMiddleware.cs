using System.Text;
using Trustlist.Api.Signing;

namespace Trustlist.Api.Middleware;

/// <summary>
/// MAS-696 — wraps successful JSON responses on the public role-keyed directory
/// surface (<c>/v1/issuers/...</c>, <c>/v1/wallet-providers/...</c>,
/// <c>/v1/verifiers/...</c>) in a JWS-compact form
/// (<c>base64url(header).base64url(payload).base64url(signature)</c>).
///
/// Mechanism:
///   1. Swap <see cref="HttpResponse.Body"/> for an in-memory stream so we can
///      capture the JSON the controller wrote.
///   2. Invoke the rest of the pipeline.
///   3. If the response status is 2xx, the path matches one of the signed
///      surfaces, and the captured body is JSON, replace the buffered bytes with
///      <c>signer.Sign(captured)</c> and set <c>Content-Type: application/jwt</c>.
///   4. Non-matching paths, non-2xx, or non-JSON bodies pass through unchanged.
///
/// Excluded by design:
///   - <c>/health</c> — liveness probe, must not be signed (load balancers parse
///     it).
///   - <c>/.well-known/trustlist-jwks.json</c> — the JWKS is the trust anchor;
///     signing it would be circular.
///   - <c>/swagger*</c> — Swagger UI / OpenAPI doc.
///   - <c>/api/trustlist/...</c>, <c>/api/auth/...</c> — legacy admin / auth
///     surfaces out of scope per MAS-696.
///   - Any non-2xx response (4xx / 5xx) — pass through; clients see the same
///     error shape they always have.
/// </summary>
public class JwsResponseMiddleware
{
    private readonly RequestDelegate _next;
    private readonly TLPublisherSigner _signer;
    private readonly ILogger<JwsResponseMiddleware> _logger;

    public JwsResponseMiddleware(
        RequestDelegate next,
        TLPublisherSigner signer,
        ILogger<JwsResponseMiddleware> logger)
    {
        _next = next;
        _signer = signer;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Decide up-front whether this request is a candidate for signing. If it
        // is not, run the pipeline normally — no buffering, no overhead.
        if (!ShouldSign(context))
        {
            await _next(context);
            return;
        }

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);

            buffer.Position = 0;
            var captured = buffer.ToArray();

            // Only sign on a successful 2xx response with a JSON body. 4xx / 5xx
            // pass through with the original error JSON (the controller's status
            // is what carries the error semantics).
            if (context.Response.StatusCode >= 200 && context.Response.StatusCode < 300
                && captured.Length > 0
                && LooksLikeJson(captured))
            {
                var jws = _signer.Sign(captured);
                var jwsBytes = Encoding.UTF8.GetBytes(jws);

                context.Response.Body = originalBody;
                context.Response.ContentLength = jwsBytes.Length;
                context.Response.ContentType = "application/jwt";
                await context.Response.Body.WriteAsync(jwsBytes);
            }
            else
            {
                context.Response.Body = originalBody;
                await context.Response.Body.WriteAsync(captured);
            }
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static bool ShouldSign(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)) return false;
        if (!context.Request.Path.StartsWithSegments("/v1/issuers", StringComparison.OrdinalIgnoreCase)
            && !context.Request.Path.StartsWithSegments("/v1/wallet-providers", StringComparison.OrdinalIgnoreCase)
            && !context.Request.Path.StartsWithSegments("/v1/verifiers", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    private static bool LooksLikeJson(ReadOnlySpan<byte> body)
    {
        // Trim leading whitespace, then look at the first non-whitespace byte.
        // JSON-as-text starts with '{' or '['; if the controller wrote something
        // else (e.g. an empty body or a stream the caller reset) skip signing.
        for (var i = 0; i < body.Length; i++)
        {
            var b = body[i];
            if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n') continue;
            return b == (byte)'{' || b == (byte)'[';
        }
        return false;
    }
}