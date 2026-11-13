using Microsoft.EntityFrameworkCore;

namespace LogisysOrgConverter.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<CountryAlias> CountryAliases => Set<CountryAlias>();

    public DbSet<KnownState> KnownStates => Set<KnownState>();

    public DbSet<FieldMap> FieldMaps => Set<FieldMap>();

    public DbSet<ConversionRun> Runs => Set<ConversionRun>();

    public DbSet<ConversionRunRow> RunRows => Set<ConversionRunRow>();

    public DbSet<EmailNotification> EmailNotifications => Set<EmailNotification>();

    public DbSet<ExistingOrganisation> ExistingOrganisations => Set<ExistingOrganisation>();

    public DbSet<GoogleConnection> GoogleConnections => Set<GoogleConnection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CountryAlias>(entity =>
        {
            entity.Property(item => item.Alias).HasMaxLength(200);
            entity.Property(item => item.AliasKey).HasMaxLength(200);
            entity.Property(item => item.CanonicalName).HasMaxLength(200);
            entity.HasIndex(item => item.AliasKey).IsUnique();
        });

        modelBuilder.Entity<KnownState>(entity =>
        {
            entity.Property(item => item.CountryCanonical).HasMaxLength(200);
            entity.Property(item => item.Name).HasMaxLength(120);
            entity.Property(item => item.Abbreviation).HasMaxLength(20);
            entity.Property(item => item.GstCode).HasMaxLength(2);
        });

        modelBuilder.Entity<FieldMap>(entity =>
        {
            entity.Property(item => item.TemplateCode).HasMaxLength(20);
            entity.Property(item => item.TargetHeader).HasMaxLength(120);
            entity.Property(item => item.Transform).HasMaxLength(40);
            entity.Property(item => item.SourceColumn).HasMaxLength(8);
            entity.Property(item => item.ConstantValue).HasMaxLength(40);
            entity.Property(item => item.FlagKey).HasMaxLength(40);
            entity.Property(item => item.FallbackColumn).HasMaxLength(8);
        });

        modelBuilder.Entity<ConversionRun>(entity =>
        {
            entity.Property(item => item.SourceFileName).HasMaxLength(260);
            entity.HasMany(item => item.Rows)
                .WithOne()
                .HasForeignKey(item => item.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConversionRunRow>(entity =>
        {
            entity.Property(item => item.OrganisationName).HasMaxLength(300);
            entity.Property(item => item.Status).HasMaxLength(20);
            entity.Property(item => item.Template).HasMaxLength(20);
            entity.Property(item => item.BranchName).HasMaxLength(200);
            entity.Property(item => item.City).HasMaxLength(200);
            entity.Property(item => item.State).HasMaxLength(200);
            entity.Property(item => item.Country).HasMaxLength(200);
            entity.Property(item => item.RequesterEmail).HasMaxLength(320);
            entity.Property(item => item.RequesterName).HasMaxLength(200);
            entity.Property(item => item.ImportStatus).HasMaxLength(20);
            entity.HasIndex(item => item.RunId);
        });

        modelBuilder.Entity<EmailNotification>(entity =>
        {
            entity.ToTable("EmailNotifications");
            entity.Property(item => item.RecipientEmail).HasMaxLength(320);
            entity.Property(item => item.RecipientName).HasMaxLength(200);
            entity.Property(item => item.Subject).HasMaxLength(400);
            entity.Property(item => item.Delivery).HasMaxLength(20);
            entity.Property(item => item.Status).HasMaxLength(20);
            entity.HasIndex(item => item.RunId);
        });

        modelBuilder.Entity<ExistingOrganisation>(entity =>
        {
            entity.ToTable("ExistingOrganisations");
            entity.Property(item => item.Name).HasMaxLength(300);
            entity.Property(item => item.NameKey).HasMaxLength(300);
            entity.HasIndex(item => item.NameKey).IsUnique();
        });

        modelBuilder.Entity<GoogleConnection>(entity =>
        {
            entity.ToTable("GoogleConnections");
            entity.Property(item => item.Email).HasMaxLength(320);
        });
    }
}
