using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WetScrubber.Business.AI;
using WetScrubber.Business.Diagnostics;
using WetScrubber.Business.Reports;
using WetScrubber.Database;
using WetScrubber.Repositories;
using WetScrubber.Repositories.Contracts;
using WetScrubber.Repositories.Interfaces;
using WetScrubber.Repositories.Repositories;

//// ── Serilog setup ────────────────────────────────────────────────────────────
//Log.Logger = new LoggerConfiguration()
//    .WriteTo.Console()
//    .WriteTo.File("logs/wetscrubber-.log", rollingInterval: RollingInterval.Day)
//    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
//builder.Host.UseSerilog();

//  MySQL Database
// Database connection string
var mysqlstr = builder.Configuration.GetConnectionString("DefaultConnection");
// Register DbContext with MySQL
builder.Services.AddDbContextPool<ApplicationDbContext>(options =>
    options.UseMySql(mysqlstr, MySqlServerVersion.LatestSupportedServerVersion));


// ── Cookie / Login path ───────────────────────────────────────────────────────
builder.Services.Configure<GroqOptions>(
    builder.Configuration.GetSection(GroqOptions.SectionName));
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// ── MVC ───────────────────────────────────────────────────────────────────────
builder.Services.AddControllersWithViews();
//builder.Services.AddScoped<WetScrubber.Services.ScrubberCalculationEngine>();
// ── Session (for TempData, flash messages) ────────────────────────────────────
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// Paste this directly above builder.Build(); in the main WetScrubber Web project Program.cs

builder.Services.Configure<WetScrubber.Business.AI.ChemistryPredictionOptions>(
    builder.Configuration.GetSection("ChemistryPrediction"));

builder.Services.AddHttpClient<WetScrubber.Business.AI.IChemistryPredictionClient, WetScrubber.Business.AI.ChemistryPredictionClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WetScrubber.Business.AI.ChemistryPredictionOptions>>().Value;
    client.BaseAddress = new Uri(string.IsNullOrEmpty(options.BaseUrl) ? "http://localhost:8500/" : options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});



builder.Services.AddScoped<IAiPromptBuilder, AiPromptBuilder>();

builder.Services.AddScoped<IAiNarrativeService, AiNarrativeService>();

// Registers GroqChatProvider as the concrete IAiChatProvider, wired to a
// named HttpClient so BaseAddress/Timeout from GroqChatProvider's
// constructor are honored per request. Replaces the old local Ollama
// provider — same interface, hosted model instead of CPU-bound local one.
builder.Services.AddHttpClient<IAiChatProvider, GroqChatProvider>();

// ── NEW: deterministic diagnostics rule table (symptom -> diagnosis ->
// recommendation). TemplateNarrativeBuilder depends on this. ──
builder.Services.AddScoped<IDesignDiagnosticsEngine, DesignDiagnosticsEngine>();

// ── NEW: deterministic template builder (Phase 3) — was defined but never
// registered, so nothing could resolve ITemplateNarrativeBuilder before. ──
builder.Services.AddScoped<ITemplateNarrativeBuilder, TemplateNarrativeBuilder>();

// ── NEW: report persistence (Phase 3) — same situation, class existed,
// nothing registered it. ──
builder.Services.AddScoped<IDesignReportRepository, DesignReportRepository>();

#region Adding Scope and HttpClient

// Register Repositories
builder.Services.AddScoped<IUnitOfWork, UnitOfWorks>();

#endregion
var app = builder.Build();

// ── Middleware pipeline ───────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();

app.UseAuthentication();   // ← Must be BEFORE UseAuthorization
app.UseAuthorization();

// ── Default route: unauthenticated users  Login ──────────────────────────────
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");


app.Run();