using Domain;
using Microsoft.EntityFrameworkCore;

namespace Api.Data;

public static class Seeder
{
    public static async Task EnsureAsync(AppDb db)
    {
        await db.Database.EnsureCreatedAsync();
        if (!await db.Zones.AnyAsync()) await SeedAsync(db);
    }

    // 운영 데이터(주문 등)를 모두 지우고 같은 시드로 되돌린다. 감사 로그는 남긴다.
    public static async Task ResetAsync(AppDb db)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.AgentEvents.ExecuteDeleteAsync();
        await db.AgentRuns.ExecuteDeleteAsync();
        await db.IdempotencyRecords.ExecuteDeleteAsync();
        await db.Payments.ExecuteDeleteAsync();
        await db.OrderItems.ExecuteDeleteAsync();
        await db.StoreOrders.ExecuteDeleteAsync();
        await db.Orders.ExecuteDeleteAsync();
        await db.DeliveryJobs.ExecuteDeleteAsync();
        await db.Batches.ExecuteDeleteAsync();
        await db.StoreProducts.ExecuteDeleteAsync();
        await db.Products.ExecuteDeleteAsync();
        await db.AppSettings.ExecuteDeleteAsync();
        await db.ZoneDistances.ExecuteDeleteAsync();
        await db.Residents.ExecuteDeleteAsync();
        await db.Couriers.ExecuteDeleteAsync();
        await db.Stores.ExecuteDeleteAsync();
        await db.Zones.ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        await SeedAsync(db);
        await tx.CommitAsync();
    }

    static async Task SeedAsync(AppDb db)
    {
        db.Zones.AddRange(
            new Zone { Id = 1, Name = "청도읍" }, new Zone { Id = 2, Name = "금천면" }, new Zone { Id = 3, Name = "풍각면" });

        // 거리표는 가상 좌표 기반 시연 값이다 (TRD Q-03).
        foreach (var (from, to, m) in new[]
        {
            (1, 1, 4000), (1, 2, 9000), (1, 3, 12000),
            (2, 1, 9000), (2, 2, 4000), (2, 3, 8000),
            (3, 1, 12000), (3, 2, 8000), (3, 3, 4000),
        }) db.ZoneDistances.Add(new ZoneDistance { FromZoneId = from, ToZoneId = to, DistanceM = m });

        db.Residents.AddRange(
            new Resident { Id = 1, DisplayName = "가상 주민 A", ZoneId = 1, AddressLabel = "청도읍 가상로 1" },
            new Resident { Id = 2, DisplayName = "가상 주민 B", ZoneId = 2, AddressLabel = "금천면 가상로 2" },
            new Resident { Id = 3, DisplayName = "가상 주민 C", ZoneId = 3, AddressLabel = "풍각면 가상로 3" });
        db.Couriers.AddRange(
            new Courier { Id = 1, DisplayName = "가상 배송자 A" }, new Courier { Id = 2, DisplayName = "가상 배송자 B" });

        db.Stores.AddRange(
            new Store { Id = 1, Name = "탑마트 청도점", ZoneId = 1, OpenTime = new(8, 30), CloseTime = new(22, 0), ClosedRule = StoreCalendar.Sunday2And4 },
            new Store { Id = 2, Name = "동곡식자재마트", ZoneId = 2, OpenTime = new(7, 0), CloseTime = new(22, 30), ClosedRule = StoreCalendar.None },
            new Store { Id = 3, Name = "풍각식자재마트", ZoneId = 3, OpenTime = new(7, 0), CloseTime = new(22, 30), ClosedRule = StoreCalendar.None });

        var seed = new (int Id, string Name, string Unit, string Keys, int[] Price, int[] Stock)[]
        {
            (1, "두부", "모", "두부", new[] { 2200, 1900, 2000 }, new[] { 20, 15, 10 }),
            (2, "대파", "단", "대파,파", new[] { 2800, 3200, 2500 }, new[] { 15, 10, 12 }),
            (3, "계란 30구", "판", "계란,달걀", new[] { 7900, 7500, 8200 }, new[] { 10, 8, 5 }),
            (4, "우유 1L", "개", "우유", new[] { 2900, 2700, 2800 }, new[] { 20, 20, 20 }),
            (5, "라면 5입", "봉", "라면", new[] { 4200, 3900, 4100 }, new[] { 30, 25, 20 }),
            (6, "쌀 10kg", "포", "쌀", new[] { 29000, 27500, 28000 }, new[] { 8, 6, 3 }),
            (7, "양파 1kg", "망", "양파", new[] { 2500, 2300, 2600 }, new[] { 20, 15, 10 }),
            (8, "콩나물", "봉", "콩나물", new[] { 1500, 1300, 1400 }, new[] { 12, 12, 12 }),
        };
        foreach (var s in seed)
        {
            db.Products.Add(new Product { Id = s.Id, Name = s.Name, Unit = s.Unit, Keywords = s.Keys, Category = "생필품" });
            for (var i = 0; i < 3; i++)
                db.StoreProducts.Add(new StoreProduct { StoreId = i + 1, ProductId = s.Id, Price = s.Price[i], StockQty = s.Stock[i] });
        }

        db.AppSettings.AddRange(
            new AppSetting { Key = SettingKeys.BaseFee, Value = "3000" },
            new AppSetting { Key = SettingKeys.PerStopFee, Value = "500" },
            new AppSetting { Key = SettingKeys.EarnPerKm, Value = "400" },
            new AppSetting { Key = SettingKeys.EarnBase, Value = "1500" },
            new AppSetting { Key = SettingKeys.AiEnabled, Value = "true" });
        await db.SaveChangesAsync();
    }
}
