using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VolleyballClub.Domain.Entities;

namespace VolleyballClub.Infrastructure.Persistence.Configurations;

public class SemesterMemberConfiguration : IEntityTypeConfiguration<SemesterMember>
{
    public void Configure(EntityTypeBuilder<SemesterMember> builder)
    {
        builder.ToTable("SemesterMembers");

        builder.Property(m => m.Label).HasMaxLength(200);
        builder.Property(m => m.Name).HasMaxLength(100);

        // 계정×학기당 매핑 행 1개 — 동시 출석의 최종 DB 방어(선검사와 이중)
        builder.HasIndex(m => new { m.MappedUserId, m.SemesterId })
            .IsUnique()
            .HasFilter("\"MappedUserId\" IS NOT NULL AND \"SemesterId\" IS NOT NULL");

        builder.HasIndex(m => m.MappedUserId);
        builder.HasIndex(m => m.SemesterId);

        builder.HasOne(m => m.Semester)
            .WithMany()
            .HasForeignKey(m => m.SemesterId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class SemesterAttendanceConfiguration : IEntityTypeConfiguration<SemesterAttendance>
{
    public void Configure(EntityTypeBuilder<SemesterAttendance> builder)
    {
        builder.ToTable("SemesterAttendances");

        // 같은 행에 같은 활동 중복 방지 — 재가져오기 멱등과 동시 출석 방어
        builder.HasIndex(a => new { a.SemesterMemberId, a.ActivityId }).IsUnique();

        builder.HasIndex(a => a.ActivityId);

        builder.HasOne(a => a.SemesterMember)
            .WithMany(m => m.Attendances)
            .HasForeignKey(a => a.SemesterMemberId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.Activity)
            .WithMany(a => a.SemesterAttendances)
            .HasForeignKey(a => a.ActivityId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
