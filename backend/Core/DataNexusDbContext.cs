using Microsoft.EntityFrameworkCore;

namespace DataNexus.Core;

public sealed class DataNexusDbContext(DbContextOptions<DataNexusDbContext> options)
    : DbContext(options)
{
    public DbSet<SkillEntity> Skills => Set<SkillEntity>();
    public DbSet<AgentEntity> Agents => Set<AgentEntity>();
    public DbSet<PipelineEntity> Pipelines => Set<PipelineEntity>();
    public DbSet<OrchestrationEntity> Orchestrations => Set<OrchestrationEntity>();
    public DbSet<TaskHistoryEntity> TaskHistory => Set<TaskHistoryEntity>();

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

        modelBuilder.Entity<AgentEntity>(entity =>
        {
            entity.ToTable("agents");

            entity.HasIndex(e => new { e.Name, e.OwnerId })
                  .IsUnique();

            entity.HasIndex(e => e.Scope);

            entity.Property(e => e.Scope)
                  .HasConversion<string>()
                  .HasMaxLength(20);

            entity.Property(e => e.ExecutionType)
                  .HasConversion<string>()
                  .HasMaxLength(20)
                  .HasDefaultValue(AgentExecutionType.Llm);

            entity.Property(e => e.TimeoutSeconds)
                  .HasDefaultValue(30);
        });

        modelBuilder.Entity<PipelineEntity>(entity =>
        {
            entity.ToTable("pipelines");

            entity.HasIndex(e => new { e.Name, e.OwnerId })
                  .IsUnique();

            entity.HasIndex(e => e.Scope);

            entity.Property(e => e.Scope)
                  .HasConversion<string>()
                  .HasMaxLength(20);

            entity.Property(e => e.MaxCorrectionAttempts)
                  .HasDefaultValue(3);
        });

        modelBuilder.Entity<OrchestrationEntity>(entity =>
        {
            entity.ToTable("orchestrations");

            entity.HasIndex(e => new { e.Name, e.OwnerId })
                  .IsUnique();

            entity.HasIndex(e => e.Scope);
            entity.HasIndex(e => e.Status);

            entity.Property(e => e.Scope)
                  .HasConversion<string>()
                  .HasMaxLength(20);

            entity.Property(e => e.Status)
                  .HasConversion<string>()
                  .HasMaxLength(20);

            entity.Property(e => e.MaxCorrectionAttempts)
                  .HasDefaultValue(3);
        });

        modelBuilder.Entity<TaskHistoryEntity>(entity =>
        {
            entity.ToTable("task_history");

            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.CreatedAt);
        });
    }
}
