using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VolleyballClub.Domain.Entities;

namespace VolleyballClub.Infrastructure.Persistence.Configurations;

public class SemesterPeriodConfiguration : IEntityTypeConfiguration<SemesterPeriod>
{
    public void Configure(EntityTypeBuilder<SemesterPeriod> builder)
    {
        builder.ToTable("SemesterPeriods");

        builder.HasIndex(p => p.StartDate);
    }
}
