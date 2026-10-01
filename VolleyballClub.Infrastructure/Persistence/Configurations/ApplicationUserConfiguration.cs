using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VolleyballClub.Infrastructure.Identity;

namespace VolleyballClub.Infrastructure.Persistence.Configurations;

public class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        // 학번 중복 등록을 DB에서 차단
        builder.HasIndex(u => u.StudentNumber).IsUnique();
    }
}
