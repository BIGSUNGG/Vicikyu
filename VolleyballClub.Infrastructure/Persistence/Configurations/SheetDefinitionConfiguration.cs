using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VolleyballClub.Domain.Entities;

namespace VolleyballClub.Infrastructure.Persistence.Configurations;

/// <summary>시트 정의 — 종류 이름은 유일.</summary>
public class SheetDefinitionConfiguration : IEntityTypeConfiguration<SheetDefinition>
{
    public void Configure(EntityTypeBuilder<SheetDefinition> builder)
    {
        builder.HasIndex(d => d.Name).IsUnique();
    }
}
