using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Infrastructure.Identity;

namespace VolleyballClub.Infrastructure.Persistence;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Activity> Activities => Set<Activity>();

    public DbSet<SemesterMember> SemesterMembers => Set<SemesterMember>();

    public DbSet<SemesterAttendance> SemesterAttendances => Set<SemesterAttendance>();

    public DbSet<Guest> Guests => Set<Guest>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();

    public DbSet<FeePeriod> FeePeriods => Set<FeePeriod>();

    public DbSet<SemesterPeriod> SemesterPeriods => Set<SemesterPeriod>();

    public DbSet<FeePayment> FeePayments => Set<FeePayment>();

    public DbSet<Setting> Settings => Set<Setting>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<DayMark> DayMarks => Set<DayMark>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
        modelBuilder.Entity<DayMark>().HasKey(m => m.Date);
        modelBuilder.Entity<DayMark>().ToTable("DayMarks");
    }
}
