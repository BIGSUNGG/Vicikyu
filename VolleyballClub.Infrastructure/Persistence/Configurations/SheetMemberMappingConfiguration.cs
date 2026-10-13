using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VolleyballClub.Domain.Entities;

namespace VolleyballClub.Infrastructure.Persistence.Configurations;

/// <summary>탭별 부원 매핑 — 한 행에 한 부원, 한 부원이 한 탭에서 한 행만 연결.</summary>
public class SheetMemberMappingConfiguration : IEntityTypeConfiguration<SheetMemberMapping>
{
    public void Configure(EntityTypeBuilder<SheetMemberMapping> builder)
    {
        builder.HasIndex(m => new { m.SheetDefinitionId, m.TabName, m.RowLabel }).IsUnique();
        builder.HasIndex(m => new { m.SheetDefinitionId, m.TabName, m.UserId }).IsUnique();

        builder.HasOne(m => m.SheetDefinition)
            .WithMany()
            .HasForeignKey(m => m.SheetDefinitionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
