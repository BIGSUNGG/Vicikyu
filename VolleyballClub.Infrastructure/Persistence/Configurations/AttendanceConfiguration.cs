using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VolleyballClub.Domain.Entities;

namespace VolleyballClub.Infrastructure.Persistence.Configurations;

public class AttendanceConfiguration : IEntityTypeConfiguration<Attendance>
{
    public void Configure(EntityTypeBuilder<Attendance> builder)
    {
        builder.ToTable("Attendances");

        // 동시 출석 요청이 서버 검증을 뚫어도 DB가 최종 차단한다
        builder.HasIndex(a => new { a.ActivityId, a.UserId }).IsUnique();

        builder.HasIndex(a => a.UserId);
    }
}
