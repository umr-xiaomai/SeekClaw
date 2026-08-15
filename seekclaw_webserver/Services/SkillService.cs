using System.Text;
using Microsoft.EntityFrameworkCore;
using seekclaw_webserver.Data;
using seekclaw_webserver.Models;

namespace seekclaw_webserver.Services;

public sealed class SkillService(AppDbContext db)
{
    public async Task<List<SkillSummary>> ListAsync(bool includeDisabled)
    {
        var query = db.Skills.AsNoTracking();
        if (!includeDisabled)
        {
            query = query.Where(skill => skill.Enabled);
        }

        var skills = await query.ToListAsync();
        return skills
            .OrderByDescending(skill => skill.UpdatedAt)
            .Select(skill => new SkillSummary(
                skill.Id,
                skill.Name,
                skill.Slug,
                skill.Summary,
                skill.Author,
                skill.Version,
                skill.Enabled,
                skill.PackageData != null && skill.PackageData.Length > 0,
                skill.UpdatedAt))
            .ToList();
    }

    public async Task<SkillDetailModel?> GetDetailAsync(int id)
    {
        var skill = await db.Skills.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        return skill is null ? null : ToDetail(skill);
    }

    public async Task<Skill?> GetAsync(int id)
    {
        return await db.Skills.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
    }

    public async Task<Skill> CreateAsync(
        SkillInput input,
        byte[]? packageData,
        string? packageFileName,
        string? packageContentType)
    {
        var slug = await EnsureUniqueSlugAsync(Slugify(input.Slug, input.Name));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var skill = new Skill
        {
            Name = input.Name.Trim(),
            Slug = slug,
            Summary = input.Summary.Trim(),
            ReadmeMarkdown = input.ReadmeMarkdown,
            Author = input.Author.Trim(),
            Version = NormalizeVersion(input.Version),
            Homepage = NormalizeNull(input.Homepage),
            Enabled = input.Enabled,
            PackageData = packageData,
            PackageFileName = packageFileName,
            PackageContentType = packageContentType,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Skills.Add(skill);
        await db.SaveChangesAsync();
        return skill;
    }

    public async Task<Skill?> UpdateAsync(
        int id,
        SkillInput input,
        byte[]? packageData,
        string? packageFileName,
        string? packageContentType,
        bool replacePackage)
    {
        var skill = await db.Skills.SingleOrDefaultAsync(x => x.Id == id);
        if (skill is null)
        {
            return null;
        }

        var slug = await EnsureUniqueSlugAsync(Slugify(input.Slug, input.Name), id);
        skill.Name = input.Name.Trim();
        skill.Slug = slug;
        skill.Summary = input.Summary.Trim();
        skill.ReadmeMarkdown = input.ReadmeMarkdown;
        skill.Author = input.Author.Trim();
        skill.Version = NormalizeVersion(input.Version);
        skill.Homepage = NormalizeNull(input.Homepage);
        skill.Enabled = input.Enabled;
        skill.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (replacePackage)
        {
            skill.PackageData = packageData;
            skill.PackageFileName = packageFileName;
            skill.PackageContentType = packageContentType;
        }

        await db.SaveChangesAsync();
        return skill;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var skill = await db.Skills.SingleOrDefaultAsync(x => x.Id == id);
        if (skill is null)
        {
            return false;
        }

        db.Skills.Remove(skill);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetEnabledAsync(int id, bool enabled)
    {
        var skill = await db.Skills.SingleOrDefaultAsync(x => x.Id == id);
        if (skill is null)
        {
            return false;
        }

        skill.Enabled = enabled;
        skill.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<Skill?> GetPackageAsync(int id)
    {
        return await db.Skills.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.PackageData != null);
    }

    private async Task<string> EnsureUniqueSlugAsync(string baseSlug, int? exceptId = null)
    {
        var slug = baseSlug;
        var suffix = 2;
        while (await db.Skills.AnyAsync(x => x.Slug == slug && (!exceptId.HasValue || x.Id != exceptId.Value)))
        {
            slug = $"{baseSlug}-{suffix++}";
        }

        return slug;
    }

    private static string Slugify(string requested, string name)
    {
        var source = string.IsNullOrWhiteSpace(requested) ? name : requested;
        var builder = new StringBuilder();
        foreach (var ch in source.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "skill" : slug;
    }

    private static string NormalizeVersion(string version)
    {
        var value = version.Trim();
        return string.IsNullOrWhiteSpace(value) ? "1.0.0" : value;
    }

    private static string? NormalizeNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static SkillDetailModel ToDetail(Skill skill) =>
        new(
            skill.Id,
            skill.Name,
            skill.Slug,
            skill.Summary,
            skill.ReadmeMarkdown,
            skill.Author,
            skill.Version,
            skill.Homepage,
            skill.Enabled,
            skill.PackageData != null && skill.PackageData.Length > 0,
            skill.PackageFileName,
            skill.CreatedAt,
            skill.UpdatedAt);
}



