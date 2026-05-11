using Microsoft.EntityFrameworkCore;
using WorkIntel.Data.Entities;

namespace WorkIntel.Data;

/// <summary>
/// EF Core context for the WorkIntel API. Single table for now (tasks);
/// add DbSets here as new entities arrive.
/// </summary>
public sealed class WorkIntelDbContext : DbContext
{
    public WorkIntelDbContext(DbContextOptions<WorkIntelDbContext> options) : base(options)
    {
    }

    public DbSet<TaskRecord> Tasks => Set<TaskRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var task = modelBuilder.Entity<TaskRecord>();
        task.ToTable("tasks");
        task.HasKey(t => t.Id);

        task.Property(t => t.Id).HasColumnName("id");
        task.Property(t => t.UserId).HasColumnName("user_id").HasMaxLength(128).IsRequired();
        task.Property(t => t.Source).HasColumnName("source").HasMaxLength(32).IsRequired();
        task.Property(t => t.SourceMetaJson)
            .HasColumnName("source_meta_json")
            .HasColumnType("jsonb")
            .IsRequired();
        task.Property(t => t.OriginalText).HasColumnName("original_text").IsRequired();
        task.Property(t => t.Title).HasColumnName("title").HasMaxLength(512).IsRequired();
        task.Property(t => t.Description).HasColumnName("description");
        task.Property(t => t.Owner).HasColumnName("owner").HasMaxLength(128);
        task.Property(t => t.Deadline).HasColumnName("deadline").HasMaxLength(64);
        task.Property(t => t.Confidence).HasColumnName("confidence");
        task.Property(t => t.DetectedAt).HasColumnName("detected_at");
        task.Property(t => t.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
        task.Property(t => t.StatusAt).HasColumnName("status_at");
        task.Property(t => t.ExportTarget).HasColumnName("export_target").HasMaxLength(128);
        task.Property(t => t.ExportedAt).HasColumnName("exported_at");

        // Hot query paths.
        task.HasIndex(t => new { t.UserId, t.Status }).HasDatabaseName("ix_tasks_user_status");
        task.HasIndex(t => t.DetectedAt).HasDatabaseName("ix_tasks_detected_at");
    }
}
