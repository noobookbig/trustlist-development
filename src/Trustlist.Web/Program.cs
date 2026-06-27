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
