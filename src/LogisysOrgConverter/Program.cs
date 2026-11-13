using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.Data;
using LogisysOrgConverter.GoogleDrive;
using LogisysOrgConverter.Logisys;
using LogisysOrgConverter.Notifications;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddRequestTimeouts();
builder.Services.AddRazorPages();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("Logisys"),
        sql =>
        {
            sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(2), null);
            sql.CommandTimeout(120);
        }));
builder.Services.AddSingleton<OrganisationConverter>();
builder.Services.AddSingleton<TemplateWorkbookWriter>();
builder.Services.AddSingleton(new TemplatePaths
{
    India = Path.Combine(builder.Environment.ContentRootPath, "Templates", "OrgMaster-IN-Template.xlsx"),
    Overseas = Path.Combine(builder.Environment.ContentRootPath, "Templates", "OrgMaster-Overseas-Template.xlsx")
});
builder.Services.AddScoped<ConversionStore>();
builder.Services.AddScoped<ExistingOrganisationStore>();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.Section));
builder.Services.Configure<MailboxOptions>(builder.Configuration.GetSection(MailboxOptions.Section));
builder.Services.Configure<LogisysOrgConverter.Notifications.GoogleOptions>(
    builder.Configuration.GetSection(LogisysOrgConverter.Notifications.GoogleOptions.Section));
builder.Services.Configure<LogisysOrgConverter.Notifications.GoogleVisitorOptions>(
    builder.Configuration.GetSection(LogisysOrgConverter.Notifications.GoogleVisitorOptions.Section));
builder.Services.AddHttpClient(FormMailbox.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(40));
builder.Services.AddHttpClient(GoogleSheets.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddSingleton<FormMailbox>();
builder.Services.AddSingleton<GoogleBrowserSession>();
builder.Services.AddSingleton<GoogleSheets>();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(20);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
});
builder.Services.AddSingleton<MailDelivery>();
builder.Services.AddScoped<NotificationStore>();
builder.Services.Configure<LogisysOrgConverter.GoogleDrive.GoogleOptions>(
    builder.Configuration.GetSection(LogisysOrgConverter.GoogleDrive.GoogleOptions.Section));
builder.Services.AddHttpClient(GoogleDriveAccess.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<GoogleDriveAccess>();
builder.Services.Configure<MicrosoftOptions>(builder.Configuration.GetSection(MicrosoftOptions.Section));
builder.Services.Configure<LogisysOrgConverter.GoogleDrive.GoogleVisitorOptions>(
    builder.Configuration.GetSection(LogisysOrgConverter.GoogleDrive.GoogleVisitorOptions.Section));
builder.Services.AddHttpClient(OutlookCodeReader.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<OutlookCodeReader>();
builder.Services.AddSingleton<GoogleVisitorAccess>();
builder.Services.AddScoped<GstCertificateReader>();
builder.Services.Configure<LogisysOptions>(builder.Configuration.GetSection(LogisysOptions.Section));
builder.Services.AddSingleton<LogisysBrowserSession>();
builder.Services.AddScoped<OrganisationNoteUploader>();
builder.Services.AddScoped<OrganisationCodMarker>();
builder.Services.AddScoped<OrganisationLedgerLinker>();
builder.Services.AddScoped<OrganisationImportUploader>();
builder.Services.AddScoped<SoftlinkOrganisationFinisher>();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 20 * 1024 * 1024;
});

var app = builder.Build();

await PrepareDatabaseAsync(app);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseRequestTimeouts();
app.UseSession();
app.MapRazorPages();
app.Run();

static async Task PrepareDatabaseAsync(WebApplication app)
{
    const int attempts = 15;
    for (var attempt = 1; attempt <= attempts; attempt++)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrade.ApplyAsync(db);
            await DbInitializer.SeedAsync(db);
            return;
        }
        catch (Exception) when (attempt < attempts)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
