using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VolleyballClub.Domain.Entities;

namespace VolleyballClub.Infrastructure.Persistence.Configurations;

public class ActivityConfiguration : IEntityTypeConfiguration<Activity>
{
    public void Configure(EntityTypeBuilder<Activity> builder)
    {
        builder.ToTable("Activities");

        // 하루에 기본 활동 1개
        builder.HasIndex(a => a.ActivityDate).IsUnique();

        builder.Property(a => a.OtpCode).HasMaxLength(10);

        builder.HasMany(a => a.Attendances)
            .WithOne(a => a.Activity)
            .HasForeignKey(a => a.ActivityId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(a => a.Guests)
            .WithOne(g => g.Activity)
            .HasForeignKey(g => g.ActivityId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(a => a.Teams)
            .WithOne(t => t.Activity)
            .HasForeignKey(t => t.ActivityId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
