using Microsoft.EntityFrameworkCore;

namespace LogisysOrgConverter.Data;

// EnsureCreated only builds an empty database, so columns and tables added later are brought in here.
public static class SchemaUpgrade
{
    private static readonly string[] RunRowColumns =
    [
        "IF COL_LENGTH('dbo.RunRows', 'RequesterEmail') IS NULL ALTER TABLE dbo.RunRows ADD [RequesterEmail] nvarchar(320) NULL;",
        "IF COL_LENGTH('dbo.RunRows', 'RequesterName') IS NULL ALTER TABLE dbo.RunRows ADD [RequesterName] nvarchar(200) NULL;",
        "IF COL_LENGTH('dbo.RunRows', 'ImportStatus') IS NULL ALTER TABLE dbo.RunRows ADD [ImportStatus] nvarchar(20) NULL;",
        "IF COL_LENGTH('dbo.RunRows', 'ImportMessage') IS NULL ALTER TABLE dbo.RunRows ADD [ImportMessage] nvarchar(max) NULL;",
        "IF COL_LENGTH('dbo.RunRows', 'ImportUpdatedAtUtc') IS NULL ALTER TABLE dbo.RunRows ADD [ImportUpdatedAtUtc] datetime2 NULL;",
        "IF COL_LENGTH('dbo.RunRows', 'EmailedAtUtc') IS NULL ALTER TABLE dbo.RunRows ADD [EmailedAtUtc] datetime2 NULL;"
    ];

    public static async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        foreach (var statement in RunRowColumns)
        {
            await db.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID('dbo.EmailNotifications', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.EmailNotifications (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmailNotifications PRIMARY KEY,
                    [RunId] uniqueidentifier NOT NULL,
                    [RecipientEmail] nvarchar(320) NOT NULL,
                    [RecipientName] nvarchar(200) NULL,
                    [Organisations] nvarchar(max) NOT NULL,
                    [Subject] nvarchar(400) NOT NULL,
                    [Body] nvarchar(max) NOT NULL,
                    [Delivery] nvarchar(20) NOT NULL,
                    [Status] nvarchar(20) NOT NULL,
                    [Error] nvarchar(max) NULL,
                    [CreatedAtUtc] datetime2 NOT NULL
                );
                CREATE INDEX IX_EmailNotifications_RunId ON dbo.EmailNotifications ([RunId]);
            END
            """,
            cancellationToken);
    }
}
