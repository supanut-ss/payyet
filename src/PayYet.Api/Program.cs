using Microsoft.EntityFrameworkCore;
using PayYet.Config;
using PayYet.Data;
using PayYet.Handlers;
using PayYet.Line;
using PayYet.Services;

var builder = WebApplication.CreateBuilder(args);

// Optional machine-local overrides (secrets) that must never be committed.
builder.Configuration.AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false);

builder.Services.Configure<AppSettings>(builder.Configuration);
builder.Services.AddControllers();

// The web app lives on its own subdomain (App:WebBaseUrl) and calls this API from the browser.
var webOrigin = (builder.Configuration["App:WebBaseUrl"] ?? "https://payyet.drivetodev.online").TrimEnd('/');
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(webOrigin)
    .WithHeaders("Authorization", "Content-Type")
    .WithMethods("GET", "POST")));

var connectionString = builder.Configuration.GetConnectionString("Default") ?? "";
var serverVersion = builder.Configuration["Db:ServerVersion"] ?? "11.8.8-mariadb";   // e.g. "10.6.0-mariadb" if the host runs MariaDB
builder.Services.AddDbContext<AppDbContext>(o => o
    .UseMySql(connectionString, ServerVersion.Parse(serverVersion))
    .UseSnakeCaseNamingConvention());

builder.Services.AddHttpClient<LineClient>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<MemberService>();
builder.Services.AddScoped<BillService>();
builder.Services.AddScoped<BillManageService>();
builder.Services.AddScoped<BillCreationService>();
builder.Services.AddScoped<DraftService>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<BillCardBuilder>();
builder.Services.AddScoped<DraftCardBuilder>();
builder.Services.AddScoped<ReminderService>();
builder.Services.AddScoped<WebhookProcessor>();

var app = builder.Build();

// Only needed when the app is hosted in a sub-folder (App:PathBase, e.g. "/lineapi"); empty on a dedicated (sub)domain.
var pathBase = builder.Configuration["App:PathBase"] ?? "";
if (!string.IsNullOrEmpty(pathBase)) app.UsePathBase(pathBase);

app.UseDefaultFiles();   // "/" serves wwwroot/index.html (API status page)
app.UseStaticFiles();
app.UseCors();
app.MapControllers();

app.MapGet("/ping", (HttpContext ctx) => Results.Json(new
{
    ok = true,
    utc = DateTime.UtcNow,
    pathBase = ctx.Request.PathBase.Value,
    path = ctx.Request.Path.Value,
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    is64Bit = Environment.Is64BitProcess
}));

// Used by the deploy script: 200 only when the app started with its config and can reach the database.
app.MapGet("/health", async (AppDbContext db) =>
    await db.Database.CanConnectAsync()
        ? Results.Json(new { ok = true, db = true })
        : Results.Json(new { ok = false, db = false }, statusCode: 503));

// The LIFF page moved to the web app; keep old LIFF endpoint links working (query string, incl. liff.state, is preserved).
app.MapGet("/liff/pay", (HttpContext ctx) =>
    Results.Redirect($"{webOrigin}/liff/pay.html{ctx.Request.QueryString}"));

app.Run();

public partial class Program;
