using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VolleyballClub.Domain.Entities;

namespace VolleyballClub.Infrastructure.Persistence.Configurations;

public class FeePeriodConfiguration : IEntityTypeConfiguration<FeePeriod>
{
    public void Configure(EntityTypeBuilder<FeePeriod> builder)
    {
        builder.ToTable("FeePeriods");

        builder.HasIndex(p => p.StartDate);

        builder.HasMany(p => p.Payments)
            .WithOne(p => p.FeePeriod)
            .HasForeignKey(p => p.FeePeriodId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
