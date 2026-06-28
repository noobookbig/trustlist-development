using System.Reflection;

namespace Trustlist.Api;

/// <summary>
/// MAS-696 follow-up — exposes the API's build version. Reads from
/// <c>AssemblyInformationalVersionAttribute</c> (set via the
/// <c>&lt;Version&gt;</c> csproj property) and surfaces a short commit SHA
/// from the <c>GIT_SHA</c> env var when present (CI sets it via
/// <c>--build-arg GIT_SHA=$(git rev-parse --short HEAD)</c>).
///
/// Convention (CEO 2026-06-28, MAS-696 thread): every code change bumps
/// the version — patch for fixes, minor for features, major for breaking.
/// The <c>/version</c> endpoint lets a client pin to a specific build so
/// the signed <c>/v1/{role}/...</c> responses are reproducible against a
/// known JWKS.
/// </summary>
public static class VersionEndpoint
{
    public static IResult Handler()
    {
        var asm = typeof(VersionEndpoint).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString()
                   ?? "0.0.0";
        // Strip any +<sha> suffix that SemVer 2.0 build metadata uses — we surface
        // the SHA separately.
        var plus = info.IndexOf('+');
        var version = plus >= 0 ? info[..plus] : info;

        var gitSha = Environment.GetEnvironmentVariable("GIT_SHA") ?? "";
        if (gitSha.Length > 12) gitSha = gitSha[..12]; // short SHA, matches `git rev-parse --short`

        return Results.Json(new
        {
            service = "trustlist-api",
            version,
            build_sha = string.IsNullOrEmpty(gitSha) ? null : gitSha,
            aspnetcore = System.Environment.Version.ToString(),
            framework = "net8.0",
        }, contentType: "application/json");
    }
}