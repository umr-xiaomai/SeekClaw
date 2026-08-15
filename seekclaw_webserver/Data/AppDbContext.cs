using Microsoft.EntityFrameworkCore;
using seekclaw_webserver.Models;

namespace seekclaw_webserver.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<SiteSetting> SiteSettings => Set<SiteSetting>();
    public DbSet<Skill> Skills => Set<Skill>();

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
            entity.Property(x => x.PackageFileName).HasMaxLength(256);
            entity.Property(x => x.PackageContentType).HasMaxLength(128);
            entity.HasIndex(x => x.Slug).IsUnique();
        });
    }
}
