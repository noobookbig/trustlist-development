using System.Text.RegularExpressions;
using Xunit;

namespace Trustlist.Tests;

/// <summary>
/// MAS-727 / MAS-726 regression guard — "dependency db failed to start".
///
/// Root cause that bit us: the MS SQL <c>db</c> service only applies
/// <c>MSSQL_SA_PASSWORD</c> on the FIRST init of its data volume. The healthcheck
/// and the <c>api</c> connection string both read the live <c>${MSSQL_SA_PASSWORD}</c>,
/// so once the volume is stale (password regenerated in .env) the healthcheck logs
/// "Login failed for user 'sa'", never goes healthy, and <c>api</c> (which waits on
/// <c>condition: service_healthy</c>) refuses to start with the terse
/// "dependency failed to start" message.
///
/// These tests don't (and can't) detect a stale volume at build time — that is what
/// <c>scripts/dev-db.sh check</c> does at runtime. What they DO is lock in the
/// docker-compose invariants whose drift would (re)introduce or mask the failure:
///   1. db, api healthcheck, and api connection string all reference the SAME
///      <c>${MSSQL_SA_PASSWORD}</c> source (no hard-coded / divergent password).
///   2. api waits for the db via <c>condition: service_healthy</c> (ordering).
///   3. the db service declares a healthcheck (otherwise readiness is a guess).
/// </summary>
public class DockerComposeDbDependencyTests
{
    private static string ComposeText()
    {
        var path = FindRepoFile("docker-compose.yml");
        Assert.True(File.Exists(path), $"docker-compose.yml not found (looked from {AppContext.BaseDirectory})");
        return File.ReadAllText(path);
    }

    // Walk up from the test output dir until we find the repo root (the dir holding the file).
    private static string FindRepoFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return fileName; // let the caller's File.Exists assertion fail with context
    }

    [Fact]
    public void Compose_DbAndApi_ShareSamePasswordSource()
    {
        var text = ComposeText();

        // db service must source its sa password from the env var, not a literal.
        Assert.Matches(new Regex(@"MSSQL_SA_PASSWORD:\s*\$\{MSSQL_SA_PASSWORD"), text);

        // api connection string must use the SAME env var so it can never drift
        // from what the db was initialised with.
        Assert.Matches(new Regex(@"Password=\$\{MSSQL_SA_PASSWORD\}"), text);

        // Guard against a hard-coded sa password sneaking in (the classic way the
        // healthcheck and server passwords silently diverge).
        Assert.DoesNotMatch(new Regex(@"MSSQL_SA_PASSWORD:\s*[""']?[A-Za-z0-9]{8,}"), text);
    }

    [Fact]
    public void Compose_Api_WaitsForDbHealthy()
    {
        var text = ComposeText();
        // api must depend on db with service_healthy (correct readiness ordering).
        Assert.Matches(new Regex(@"depends_on:\s*\r?\n\s*db:\s*\r?\n\s*condition:\s*service_healthy"), text);
    }

    [Fact]
    public void Compose_Db_DeclaresHealthcheck()
    {
        var text = ComposeText();
        Assert.Contains("healthcheck:", text);
        // The healthcheck must actually authenticate (sqlcmd with the sa password),
        // so an auth failure surfaces as unhealthy rather than a false-positive.
        Assert.Matches(new Regex(@"sqlcmd.*-U sa -P"), text);
    }
}
