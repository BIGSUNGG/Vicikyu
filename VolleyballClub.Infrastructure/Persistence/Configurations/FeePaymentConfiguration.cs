using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VolleyballClub.Domain.Entities;

namespace VolleyballClub.Infrastructure.Persistence.Configurations;

public class FeePaymentConfiguration : IEntityTypeConfiguration<FeePayment>
{
    public void Configure(EntityTypeBuilder<FeePayment> builder)
    {
        builder.ToTable("FeePayments");

        // 기간별 납부 기록은 부원당 1건
        builder.HasIndex(p => new { p.FeePeriodId, p.UserId }).IsUnique();

        builder.HasIndex(p => p.UserId);
    }
}
