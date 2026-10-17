using Microsoft.EntityFrameworkCore;

namespace GameServer.Data;

public class DashboardDbContext : DbContext
{
    public DashboardDbContext(DbContextOptions<DashboardDbContext> opts) : base(opts) { }

    public DbSet<DashboardUser> Users => Set<DashboardUser>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DashboardUser>()
         .HasIndex(u => u.Username)
         .IsUnique();
    }
}
