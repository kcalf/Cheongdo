using Domain;
using Microsoft.EntityFrameworkCore;

namespace Api.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<ZoneDistance> ZoneDistances => Set<ZoneDistance>();
    public DbSet<Resident> Residents => Set<Resident>();
    public DbSet<Courier> Couriers => Set<Courier>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StoreProduct> StoreProducts => Set<StoreProduct>();
    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<StoreOrder> StoreOrders => Set<StoreOrder>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<DeliveryJob> DeliveryJobs => Set<DeliveryJob>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<AgentEvent> AgentEvents => Set<AgentEvent>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Zone>().HasIndex(x => x.Name).IsUnique();
        b.Entity<ZoneDistance>().HasKey(x => new { x.FromZoneId, x.ToZoneId });
        b.Entity<StoreProduct>().HasKey(x => new { x.StoreId, x.ProductId });
        b.Entity<StoreProduct>().ToTable(t => t.HasCheckConstraint("CK_StoreProduct_Stock", "StockQty >= 0"));
        b.Entity<Product>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Batch>().HasIndex(x => new { x.BusinessDate, x.Slot }).IsUnique();
        b.Entity<Batch>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Entity<Batch>().Property(x => x.Slot).HasConversion<string>().HasMaxLength(10);
        b.Entity<Order>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Entity<Order>().HasIndex(x => new { x.BatchId, x.Status });
        b.Entity<Order>().HasMany(x => x.Items).WithOne().HasForeignKey(x => x.OrderId);
        b.Entity<Order>().HasMany(x => x.Payments).WithOne().HasForeignKey(x => x.OrderId);
        b.Entity<OrderItem>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Entity<StoreOrder>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Entity<StoreOrder>().HasIndex(x => new { x.OrderId, x.StoreId }).IsUnique();
        b.Entity<Payment>().Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Entity<Payment>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Entity<DeliveryJob>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Entity<DeliveryJob>().HasIndex(x => new { x.BatchId, x.ZoneId }).IsUnique();
        b.Entity<AgentRun>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Entity<AgentEvent>().HasKey(x => new { x.RunId, x.EventId });
        b.Entity<IdempotencyRecord>().HasKey(x => new { x.Key, x.Endpoint, x.ActorId });
        b.Entity<AppSetting>().HasKey(x => x.Key);
    }
}
