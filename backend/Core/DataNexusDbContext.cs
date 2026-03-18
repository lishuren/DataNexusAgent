using Microsoft.EntityFrameworkCore;

namespace DataNexus.Core;

public sealed class DataNexusDbContext(DbContextOptions<DataNexusDbContext> options)
    : DbContext(options)
{
    public DbSet<SkillEntity> Skills => Set<SkillEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SkillEntity>(entity =>
        {
            entity.ToTable("skills");

            entity.HasIndex(e => new { e.Name, e.OwnerId })
                  .IsUnique();

            entity.HasIndex(e => e.Scope);

            entity.Property(e => e.Scope)
                  .HasConversion<string>()
                  .HasMaxLength(20);
        });
    }
}
