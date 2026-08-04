using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using OnlineExamPlatform.Web.Data;
using OnlineExamPlatform.Web.Models;
using OnlineExamPlatform.Web.Services;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

// Database — switch between "Local" and "Supabase" via DatabaseProvider in appsettings.json
var dbProvider = builder.Configuration.GetValue<string>("DatabaseProvider") ?? "Local";
var connectionName = dbProvider.Equals("Supabase", StringComparison.OrdinalIgnoreCase) ? "Supabase" : "DefaultConnection";

// Build the connection string and append keepalive settings so PostgreSQL
// does not silently drop idle pool connections (common on remote hosts).
var connStr = builder.Configuration.GetConnectionString(connectionName) ?? "";
if (!connStr.Contains("Keepalive", StringComparison.OrdinalIgnoreCase))
    connStr += ";Keepalive=30";
if (!connStr.Contains("Connection Idle Lifetime", StringComparison.OrdinalIgnoreCase))
    connStr += ";Connection Idle Lifetime=300";

// In-memory cache — backs the per-exam grading graph (questions/options/answer
// keys) so the end-of-exam submit burst doesn't re-read the same static rows
// once per student.
//
// NOTE: IMemoryCache is per-process. GradingGraphCacheInvalidator evicts the graph
// on the node that performed a write, so a scaled-out deployment would leave other
// nodes serving their own stale copy until it expires. This design assumes a single
// app instance; running more than one requires a distributed cache first.
builder.Services.AddMemoryCache();

// Evicts the cached grading graph whenever a Question/QuestionOption/AnswerKey is
// written, from any code path. Singleton because it holds no per-save state and is
// attached to the pooled DbContext options below.
builder.Services.AddSingleton<GradingGraphCacheInvalidator>();

// Pool DbContext instances so they are reused across requests instead of being
// allocated per request — cuts allocation/GC pressure under the synchronized
// exam start/submit bursts.
builder.Services.AddDbContextPool<ApplicationDbContext>((serviceProvider, options) =>
    options
        .AddInterceptors(serviceProvider.GetRequiredService<GradingGraphCacheInvalidator>())
        .UseNpgsql(
            connStr,
            npgsql =>
            {
                // Retry up to 3 times on transient failures (dropped idle connections,
                // momentary network blips).  Each attempt waits up to 5 s before retrying.
                npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);
                // Allow slow operations (bulk imports, Word-doc parsing) to finish
                // without the command timing out at the default 30 s.
                npgsql.CommandTimeout(120);
            }));

// Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 6;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.SignIn.RequireConfirmedAccount = false;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.AllowedForNewUsers = true;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
});

// Application services
builder.Services.AddScoped<ExamService>();
builder.Services.AddScoped<GradingService>();
builder.Services.AddScoped<CsvImportService>();
builder.Services.AddScoped<WordDocImportService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<ReportCardService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<IStudentService, StudentService>();
builder.Services.AddHttpContextAccessor();

builder.Services.AddControllersWithViews();

// Let AJAX/fetch requests supply the anti-forgery token via a request header.
// The exam page posts JSON (SaveAnswer/UpdateTimer/RecordTabSwitch) rather than
// form fields, so [ValidateAntiForgeryToken] needs a header to read. Form posts
// (which still send the __RequestVerificationToken field) are unaffected.
builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");

var app = builder.Build();

// Apply migrations and seed data
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<ApplicationDbContext>();

    // Run migrations
    try
    {
        await context.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while migrating the database.");
        throw;
    }

    // Seed roles and admin user
    await SeedData.InitializeAsync(services);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles(); // serves wwwroot (css, js, images, etc.)

// ── Upload directory ─────────────────────────────────────────────────────────
// Configured via "UploadPath" in appsettings.json / appsettings.Production.json.
//   Relative value (e.g. "uploads-data") → resolved from ContentRootPath.
//   Absolute value (e.g. "/var/webapp/uploads") → used as-is.
// The directory is created automatically on first run.
var rawUploadPath = app.Configuration["UploadPath"] ?? "uploads-data";
var uploadDirectory = Path.IsPathRooted(rawUploadPath)
    ? rawUploadPath
    : Path.Combine(app.Environment.ContentRootPath, rawUploadPath);

Directory.CreateDirectory(uploadDirectory);

// Serve all uploaded files at the "/uploads" URL regardless of where they live on disk.
// Both development (Windows) and production (Ubuntu) use the same URL pattern — /uploads/{filename}.
// X-Content-Type-Options prevents the browser from MIME-sniffing uploaded files into
// executable content types (e.g. an SVG served as image/svg+xml containing JavaScript).
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadDirectory),
    RequestPath  = "/uploads",
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
});

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
