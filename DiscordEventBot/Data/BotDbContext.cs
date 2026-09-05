using Microsoft.EntityFrameworkCore;

namespace DiscordEventBot.Data;

public sealed class BotDbContext : DbContext
{
    public BotDbContext(DbContextOptions<BotDbContext> options) : base(options)
    {
    }

    public DbSet<EventEntity> Events => Set<EventEntity>();
    public DbSet<EventRoomEntity> EventRooms => Set<EventRoomEntity>();
    public DbSet<ParticipantEntity> Participants => Set<ParticipantEntity>();
    public DbSet<PunishmentEntity> Punishments => Set<PunishmentEntity>();
    public DbSet<WarningEntity> Warnings => Set<WarningEntity>();
    public DbSet<ControlLogEntity> ControlLogs => Set<ControlLogEntity>();
    public DbSet<EventHistoryEntity> EventHistory => Set<EventHistoryEntity>();
    public DbSet<CustomEventTemplateEntity> CustomEventTemplates => Set<CustomEventTemplateEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<EventEntity>(entity =>
        {
            entity.ToTable("Events");
            entity.HasIndex(e => new { e.GuildId, e.Status });
            entity.HasIndex(e => new { e.HostUserId, e.Status });
            entity.HasIndex(e => e.VoiceChannelId);
            entity.HasIndex(e => e.CategoryChannelId);
        });

        modelBuilder.Entity<EventRoomEntity>(entity =>
        {
            entity.ToTable("EventRooms");
            entity.HasIndex(r => r.EventId);
            entity.HasIndex(r => r.VoiceChannelId);
            entity.HasOne(r => r.Event)
                .WithMany(e => e.AdditionalRooms)
                .HasForeignKey(r => r.EventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ParticipantEntity>(entity =>
        {
            entity.ToTable("Participants");
            entity.HasIndex(p => new { p.EventId, p.UserId }).IsUnique();
            entity.HasOne(p => p.Event)
                .WithMany(e => e.Participants)
                .HasForeignKey(p => p.EventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PunishmentEntity>(entity =>
        {
            entity.ToTable("Punishments");
            entity.HasIndex(p => new { p.EventId, p.TargetUserId, p.Type, p.IsActive });
            entity.HasIndex(p => new { p.IsActive, p.ExpiresAtUtc });
            entity.HasIndex(p => p.CreatedAtUtc);
            entity.HasOne(p => p.Event)
                .WithMany(e => e.Punishments)
                .HasForeignKey(p => p.EventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WarningEntity>(entity =>
        {
            entity.ToTable("Warnings");
            entity.HasIndex(w => new { w.EventId, w.TargetUserId });
            entity.HasIndex(w => w.CreatedAtUtc);
            entity.HasOne(w => w.Event)
                .WithMany(e => e.Warnings)
                .HasForeignKey(w => w.EventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ControlLogEntity>(entity =>
        {
            entity.ToTable("ControlLog");
            entity.HasIndex(c => c.EventId);
            entity.HasIndex(c => c.HostUserId);
            entity.HasIndex(c => c.ControllerUserId);
            entity.HasIndex(c => c.CreatedAtUtc);
        });

        modelBuilder.Entity<EventHistoryEntity>(entity =>
        {
            entity.ToTable("EventHistory");
            entity.HasIndex(h => h.HostUserId);
            entity.HasIndex(h => h.EndedAtUtc);
        });

        modelBuilder.Entity<CustomEventTemplateEntity>(entity =>
        {
            entity.ToTable("CustomEventTemplates");
            entity.HasIndex(t => new { t.UserId, t.LastUsedAtUtc });
        });
    }
}
