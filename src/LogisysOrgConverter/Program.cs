using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.Data;
using LogisysOrgConverter.GoogleDrive;
using LogisysOrgConverter.Notifications;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddRazorPages();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("Logisys"),
        sql => sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(2), null)));
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
builder.Services.AddSingleton<MailDelivery>();
builder.Services.AddScoped<NotificationStore>();
builder.Services.Configure<GoogleOptions>(builder.Configuration.GetSection(GoogleOptions.Section));
builder.Services.AddHttpClient(GoogleDriveAccess.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<GoogleDriveAccess>();
builder.Services.AddScoped<GstCertificateReader>();
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
