using Microsoft.EntityFrameworkCore;
using seekclaw_webserver.Models;

namespace seekclaw_webserver.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<SiteSetting> SiteSettings => Set<SiteSetting>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<SkillVersion> SkillVersions => Set<SkillVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Username).IsRequired().HasMaxLength(64);
            entity.Property(x => x.PasswordHash).IsRequired();
            entity.HasIndex(x => x.Username).IsUnique();
        });

        modelBuilder.Entity<SiteSetting>(entity =>
        {
            entity.ToTable("site_settings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).IsRequired().HasMaxLength(64);
            entity.Property(x => x.Value).IsRequired();
            entity.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<Skill>(entity =>
        {
            entity.ToTable("skills");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(128);
            entity.Property(x => x.Slug).IsRequired().HasMaxLength(128);
            entity.Property(x => x.Summary).HasMaxLength(512);
            entity.Property(x => x.ReadmeMarkdown).IsRequired();
            entity.Property(x => x.Author).HasMaxLength(128);
            entity.Property(x => x.Version).HasMaxLength(32);
            entity.Property(x => x.Homepage).HasMaxLength(512);
            entity.Property(x => x.IsOfficial).HasDefaultValue(false);
            entity.Property(x => x.AuthorUsername).HasMaxLength(64);
            entity.Property(x => x.PackageFileName).HasMaxLength(256);
            entity.Property(x => x.PackageContentType).HasMaxLength(128);
            entity.Property(x => x.ReviewStatus).IsRequired().HasMaxLength(32).HasDefaultValue(SkillReviewStatus.Approved);
            entity.Property(x => x.ReviewComment).HasMaxLength(1024);
            entity.Property(x => x.DownloadCount).HasDefaultValue(0);
            entity.Property(x => x.ViewCount).HasDefaultValue(0);
            entity.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<SkillVersion>(entity =>
        {
            entity.ToTable("skill_versions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Version).IsRequired().HasMaxLength(32);
            entity.Property(x => x.Changelog).HasMaxLength(2048);
            entity.Property(x => x.PackageFileName).HasMaxLength(256);
            entity.Property(x => x.PackageContentType).HasMaxLength(128);
            entity.Property(x => x.DownloadCount).HasDefaultValue(0);
            entity.HasIndex(x => new { x.SkillId, x.Version }).IsUnique();
            entity.HasOne(x => x.Skill)
                  .WithMany(x => x.Versions)
                  .HasForeignKey(x => x.SkillId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }

    public void EnsureSchemaUpdated()
    {
        Database.EnsureCreated();

        // 启用 WAL 模式、忙碌重试超时及正常同步级别，大幅提升高并发与高频写性能
        try
        {
            Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;");
            Database.ExecuteSqlRaw("PRAGMA busy_timeout = 5000;");
            Database.ExecuteSqlRaw("PRAGMA synchronous = NORMAL;");
        }
        catch
        {
            // 内存数据库等不支持 WAL 模式的测试环境可忽略异常
        }

        // 动态增量校验并迁移 skills 表的新字段
        using var conn = Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(skills);";
        using var reader = cmd.ExecuteReader();
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            existingColumns.Add(reader.GetString(1));
        }
        reader.Close();

        if (!existingColumns.Contains("ReviewStatus"))
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE skills ADD COLUMN ReviewStatus TEXT NOT NULL DEFAULT 'Approved';";
            alterCmd.ExecuteNonQuery();
        }

        if (!existingColumns.Contains("ReviewComment"))
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE skills ADD COLUMN ReviewComment TEXT NULL;";
            alterCmd.ExecuteNonQuery();
        }

        if (!existingColumns.Contains("DownloadCount"))
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE skills ADD COLUMN DownloadCount INTEGER NOT NULL DEFAULT 0;";
            alterCmd.ExecuteNonQuery();
        }

        if (!existingColumns.Contains("ViewCount"))
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE skills ADD COLUMN ViewCount INTEGER NOT NULL DEFAULT 0;";
            alterCmd.ExecuteNonQuery();
        }

        // 确保 skill_versions 表及索引存在
        using var versionTableCmd = conn.CreateCommand();
        versionTableCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS skill_versions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SkillId INTEGER NOT NULL,
                Version TEXT NOT NULL,
                Changelog TEXT NOT NULL,
                PackageFileName TEXT NULL,
                PackageContentType TEXT NULL,
                PackageData BLOB NULL,
                DownloadCount INTEGER NOT NULL DEFAULT 0,
                CreatedAt INTEGER NOT NULL,
                FOREIGN KEY (SkillId) REFERENCES skills (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_skill_versions_SkillId_Version ON skill_versions (SkillId, Version);
            """;
        versionTableCmd.ExecuteNonQuery();
    }
}
