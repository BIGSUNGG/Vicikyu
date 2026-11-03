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

    public DbSet<DayMark> DayMarks => Set<DayMark>();

    public DbSet<UnmappedMember> UnmappedMembers => Set<UnmappedMember>();

    public DbSet<UnmappedAttendance> UnmappedAttendances => Set<UnmappedAttendance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
        modelBuilder.Entity<DayMark>().HasKey(m => m.Date);
        modelBuilder.Entity<DayMark>().ToTable("DayMarks");
        modelBuilder.Entity<UnmappedAttendance>().HasIndex(a => new { a.UnmappedMemberId, a.ActivityId }).IsUnique();
    }
}
