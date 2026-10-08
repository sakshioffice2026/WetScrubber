using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;
using EngineeringAI.Core;
using EngineeringAI.Core.Agent;
using EngineeringAI.Core.Llm;
using WetScrubber.Business.AI;
using WetScrubber.Business.Diagnostics;
using WetScrubber.Business.Reports;
using WetScrubber.Business.Thermodynamics;
using WetScrubber.Database;
using WetScrubber.Repositories;
using WetScrubber.Repositories.Contracts;
using WetScrubber.Repositories.Interfaces;
using WetScrubber.Repositories.Repositories;
using WetScrubber.Plugins;
using WetScrubber.Services;

//// ── Serilog setup ────────────────────────────────────────────────────────────
//Log.Logger = new LoggerConfiguration()
//    .WriteTo.Console()
//    .WriteTo.File("logs/wetscrubber-.log", rollingInterval: RollingInterval.Day)
//    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
//builder.Host.UseSerilog();

var isDevelopment = builder.Environment.IsDevelopment();

// Outside Development the predictor URL must be configured explicitly;
// there is no localhost fallback.
Uri ResolvePredictorBaseUri()
{
    var configured = builder.Configuration["ChemistryPrediction:BaseUrl"];

    if (string.IsNullOrWhiteSpace(configured))
    {
        if (isDevelopment)
            return new Uri("http://localhost:8500/");

        throw new InvalidOperationException(
            "ChemistryPrediction:BaseUrl must be configured outside Development.");
    }

    return new Uri(configured.TrimEnd('/') + "/");
}

//  MySQL Database
// Database connection string
var mysqlstr = builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(mysqlstr))
{
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is not configured.");
}
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
    options.Cookie.SecurePolicy = isDevelopment
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

// ── MVC ───────────────────────────────────────────────────────────────────────
builder.Services.AddControllersWithViews();
FanSizingSettings.Load(builder.Configuration);
DesignBasisSettings.Load(builder.Configuration);
//builder.Services.AddScoped<WetScrubber.Services.ScrubberCalculationEngine>();
// ── Session (for TempData, flash messages) ────────────────────────────────────
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = isDevelopment
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

// Paste this directly above builder.Build(); in the main WetScrubber Web project Program.cs

builder.Services.Configure<WetScrubber.Business.AI.ChemistryPredictionOptions>(
    builder.Configuration.GetSection("ChemistryPrediction"));

builder.Services.AddHttpClient<WetScrubber.Business.AI.IChemistryPredictionClient, WetScrubber.Business.AI.ChemistryPredictionClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WetScrubber.Business.AI.ChemistryPredictionOptions>>().Value;
    client.BaseAddress = ResolvePredictorBaseUri();
    client.Timeout = TimeSpan.FromSeconds(5);
});

// ── Self-learning models: design-outcome calibration predictions, and the
// retrain trigger fired after curated data changes (ChemicalReaction
// promoted, DesignOutcome recorded). Same host/port as chemistry
// predictions — it's the same Python service (chemistrypredictor.py). ──
builder.Services.AddHttpClient<WetScrubber.Business.AI.IDesignOutcomePredictionClient, WetScrubber.Business.AI.DesignOutcomePredictionClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WetScrubber.Business.AI.ChemistryPredictionOptions>>().Value;
    client.BaseAddress = ResolvePredictorBaseUri();
});

builder.Services.AddHttpClient<WetScrubber.Business.AI.IModelRetrainTrigger, WetScrubber.Business.AI.ModelRetrainClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WetScrubber.Business.AI.ChemistryPredictionOptions>>().Value;
    client.BaseAddress = ResolvePredictorBaseUri();
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

// ── FIX: ChemistryUIService requires IHenrysLawLookup. ──
builder.Services.AddScoped<IHenrysLawLookup, EfHenrysLawLookup>();

builder.Services.AddScoped<WetScrubber.Services.ChemistryUIService>();

#endregion

// ── Engineering AI (local GGUF model via LLamaSharp + Semantic Kernel) ───────
builder.Services.AddEngineeringAI(options =>
    builder.Configuration.GetSection(LlamaOptions.SectionName).Bind(options));

builder.Services.AddScoped<ScrubberDatabasePlugin>();
builder.Services.AddScoped<ScrubberPhysicalChecker>();
builder.Services.AddScoped<ScrubberDesignPlugin>();
builder.Services.AddScoped<ScrubberOptimizerPlugin>();

builder.Services.AddEngineeringDomain<WetScrubberDraftState>(sp =>
{
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    return new AgentDomainDefinition<WetScrubberDraftState>
    {
        DomainDescription = ScrubberDesignPlugin.DomainDescription,
        FieldSchemaJson = ScrubberDesignPlugin.FieldSchemaJson,
        ApplyExtracted = ScrubberDesignPlugin.ApplyExtracted,
        ComputeAsync = async (state, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var plugin = scope.ServiceProvider.GetRequiredService<ScrubberDesignPlugin>();
            return await plugin.ComputeAsync(state, ct);
        }
    };
});

// Outside Development the model path must come from configuration and exist.
var llamaModelPath =
    builder.Configuration[$"{LlamaOptions.SectionName}:ModelPath"];

if (!isDevelopment &&
    (string.IsNullOrWhiteSpace(llamaModelPath) || !File.Exists(llamaModelPath)))
{
    throw new InvalidOperationException(
        $"{LlamaOptions.SectionName}:ModelPath must point to an existing model file outside Development.");
}

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