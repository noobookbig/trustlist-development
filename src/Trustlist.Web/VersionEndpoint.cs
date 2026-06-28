using System.Reflection;

namespace Trustlist.Web;

/// <summary>
/// MAS-696 follow-up — exposes the Web app's build version. Mirrors the API
/// endpoint at <c>GET /version</c> on the API so the operator dashboard can
/// show both numbers next to each other and confirm the deploy is coherent.
///
/// Convention (CEO 2026-06-28): every code change bumps <c>&lt;Version&gt;</c>
/// in the csproj. The Web version is kept in lockstep with the API so the
/// "Frontend / Backend" pair the user asked for is at the same number.
/// </summary>
public static class VersionEndpoint
{
    public static IResult Handler()
    {
        var asm = typeof(VersionEndpoint).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString()
                   ?? "0.0.0";
        var plus = info.IndexOf('+');
        var version = plus >= 0 ? info[..plus] : info;

        var gitSha = Environment.GetEnvironmentVariable("GIT_SHA") ?? "";
        if (gitSha.Length > 12) gitSha = gitSha[..12];

        return Results.Json(new
        {
            service = "trustlist-web",
            version,
            build_sha = string.IsNullOrEmpty(gitSha) ? null : gitSha,
            aspnetcore = System.Environment.Version.ToString(),
            framework = "net8.0",
        }, contentType: "application/json");
    }
}