using Trustlist.Web.Components;
using Trustlist.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var apiBaseUrl = builder.Configuration["Api:BaseUrl"] ?? "http://api:8080/";
builder.Services.AddHttpClient<TrustlistApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
});

// MAS-696 — JWS verifier (JwksCache + JwsVerifier). The base address is the
// API base, so FetchAndVerifyAsync hits /.well-known/trustlist-jwks.json and
// /v1/{role}/... on the same origin.
builder.Services.AddHttpClient<JwsVerifier>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
});
builder.Services.AddSingleton<JwksCache>();

// AuthState is per-circuit (per connected user), so use Scoped.
builder.Services.AddScoped<AuthState>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "trustlist-web" }));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
