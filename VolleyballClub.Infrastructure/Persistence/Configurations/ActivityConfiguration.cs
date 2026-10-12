using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VolleyballClub.Domain.Entities;

namespace VolleyballClub.Infrastructure.Persistence.Configurations;

public class ActivityConfiguration : IEntityTypeConfiguration<Activity>
{
    public void Configure(EntityTypeBuilder<Activity> builder)
    {
        builder.ToTable("Activities");

        // 마감된 세션과 같은 날짜 공존 허용 — 단, 진행 중(미마감) 활동은 하루 1개로 제한(동시 생성 DB 방어)
        builder.HasIndex(a => a.ActivityDate)
            .IsUnique()
            .HasFilter("\"Status\" <> 4"); // ActivityStatus.ActivityClosed 제외

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
