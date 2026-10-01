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

    public DbSet<FeePayment> FeePayments => Set<FeePayment>();

    public DbSet<Setting> Settings => Set<Setting>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
