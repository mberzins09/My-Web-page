using MartinsWeb.Components;
using MartinsWeb.Data;
using MartinsWeb.Models;
using MartinsWeb.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite("Data Source=app.db"));
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<GameService>();
builder.Services.AddScoped<UserContextService>();
builder.Services.AddScoped<LgtfDataService>();
builder.Services.AddScoped<LgtfRankingService>();
builder.Services.AddScoped<LgtfImportService>();
builder.Services.AddScoped<LgtfAdminService>();
builder.Services.AddScoped<ApiSportsService>();
builder.Services.AddHostedService<ApiScoreSyncService>();
builder.Services.AddHttpClient();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignInScheme       = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme    = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.LoginPath               = "/login";
    options.Cookie.Name             = ".MartinsWeb.Auth";
    options.Cookie.HttpOnly         = true;
    options.Cookie.SameSite         = SameSiteMode.Lax;
    options.Cookie.SecurePolicy     = CookieSecurePolicy.None;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddAuthorization();

var app = builder.Build();

// ── Database setup & seeding ───────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    // Seed default tournaments if none exist yet
    if (!db.Tournaments.Any())
    {
        db.Tournaments.AddRange(
            new Tournament { Slug = "worldcup2026", Name = "FIFA World Cup 2026",          Icon = "⚽" },
            new Tournament { Slug = "iihf2026",     Name = "IIHF World Championships 2026", Icon = "🏒" }
        );
        db.SaveChanges();
    }

    // Auto-assign any groups/games that were created before this feature
    // to "worldcup2026" (preserves existing data without admin intervention).
    var worldCup = db.Tournaments.First(t => t.Slug == "worldcup2026");

    var ungroupedGroups = db.TournamentGroups
        .Include(g => g.Games)
        .Where(g => g.TournamentId == null)
        .ToList();
    foreach (var grp in ungroupedGroups)
    {
        grp.TournamentId = worldCup.Id;
        foreach (var game in grp.Games)
            game.TournamentId = worldCup.Id;
    }

    // Single-insert games (no GroupId) also get assigned to worldcup2026
    var ungroupedGames = db.Games.Where(g => g.TournamentId == null).ToList();
    foreach (var game in ungroupedGames)
        game.TournamentId = worldCup.Id;

    if (ungroupedGroups.Any() || ungroupedGames.Any())
        db.SaveChanges();
}

// ── HTTP pipeline ──────────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// ── API endpoints ──────────────────────────────────────────────────────────────
app.MapPost("/api/login", async (HttpContext context, AuthService auth, LoginRequest req) =>
{
    var user = await auth.LoginAsync(req.Email, req.Password);
    if (user == null) return Results.Unauthorized();

    var claims = new List<Claim>
    {
        new(ClaimTypes.Name,               user.Username),
        new(ClaimTypes.NameIdentifier,     user.Id.ToString()),
        new(ClaimTypes.Role,               user.IsAdmin ? "Admin" : "User")
    };
    var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
        new AuthenticationProperties { IsPersistent = true, AllowRefresh = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7) });

    return Results.Ok();
});

app.MapPost("/api/register", async (HttpContext context, AuthService auth, RegisterRequest req) =>
{
    var user = await auth.RegisterAsync(req.Username, req.Email, req.Password);
    if (user == null) return Results.BadRequest("Email already exists");

    var claims = new List<Claim>
    {
        new(ClaimTypes.Name,               user.Username),
        new(ClaimTypes.NameIdentifier,     user.Id.ToString()),
        new(ClaimTypes.Role,               user.IsAdmin ? "Admin" : "User")
    };
    var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
        new AuthenticationProperties { IsPersistent = true, AllowRefresh = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7) });

    return Results.Ok();
});

app.MapPost("/api/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
