using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Infrastructure.Identity;

namespace VolleyballClub.Infrastructure.Persistence;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Activity> Activities => Set<Activity>();

    public DbSet<Attendance> Attendances => Set<Attendance>();

    public DbSet<Guest> Guests => Set<Guest>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();

    public DbSet<FeePeriod> FeePeriods => Set<FeePeriod>();

    public DbSet<SemesterPeriod> SemesterPeriods => Set<SemesterPeriod>();

    public DbSet<FeePayment> FeePayments => Set<FeePayment>();

    public DbSet<Setting> Settings => Set<Setting>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<SheetDefinition> SheetDefinitions => Set<SheetDefinition>();

    public DbSet<SheetMemberMapping> SheetMemberMappings => Set<SheetMemberMapping>();

    public DbSet<SheetDayMark> SheetDayMarks => Set<SheetDayMark>();

    public DbSet<ActivitySource> ActivitySources => Set<ActivitySource>();
    public DbSet<SheetEditJournal> SheetEditJournals => Set<SheetEditJournal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
        modelBuilder.Entity<SheetDayMark>().HasKey(m => m.Date);
        modelBuilder.Entity<ActivitySource>().HasKey(s => s.Id);
        modelBuilder.Entity<ActivitySource>().HasIndex(s => s.ActivityId).IsUnique();
        modelBuilder.Entity<ActivitySource>().HasOne(s => s.Activity).WithOne()
            .HasForeignKey<ActivitySource>(s => s.ActivityId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<ActivitySource>().HasOne<SheetDefinition>().WithMany()
            .HasForeignKey(s => s.SheetDefinitionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SheetEditJournal>().HasKey(j => j.Id);
    }
}
