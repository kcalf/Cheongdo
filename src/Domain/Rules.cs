namespace Domain;

public static class SettingKeys
{
    public const string BaseFee = "BaseFee";
    public const string PerStopFee = "PerStopFee";
    public const string EarnPerKm = "EarnPerKm";
    public const string EarnBase = "EarnBase";
    public const string DemoClock = "DemoClock";
    public const string AiEnabled = "AiEnabled";
}

public static class StoreCalendar
{
    public const string None = "NONE";
    public const string Sunday2And4 = "SUNDAY_2_4";

    public static bool IsOpen(string closedRule, DateOnly date)
    {
        if (closedRule != Sunday2And4 || date.DayOfWeek != DayOfWeek.Sunday) return true;
        var nth = (date.Day - 1) / 7 + 1;
        return nth != 2 && nth != 4;
    }
}

public static class Fees
{
    public static int JobFee(int baseFee, int perStopFee, int stops) => baseFee + perStopFee * stops;

    // 나머지는 앞 주문부터 1원씩 나눈다. 합계는 항상 fee와 같다.
    public static int[] Split(int fee, int stops)
    {
        if (stops <= 0) return Array.Empty<int>();
        var share = fee / stops;
        var rest = fee - share * stops;
        return Enumerable.Range(0, stops).Select(i => share + (i < rest ? 1 : 0)).ToArray();
    }

    public static int Earning(int distanceM, int earnPerKm, int earnBase) =>
        (int)Math.Round(distanceM / 1000.0 * earnPerKm + earnBase);
}

public static class BatchSchedule
{
    public static readonly TimeOnly AmCutoff = new(8, 30);
    public static readonly TimeOnly PmCutoff = new(14, 30);

    public static (DateOnly Date, BatchSlot Slot) Next(DateTime nowKst)
    {
        var date = DateOnly.FromDateTime(nowKst);
        var t = TimeOnly.FromDateTime(nowKst);
        if (t < AmCutoff) return (date, BatchSlot.AM09);
        if (t < PmCutoff) return (date, BatchSlot.PM15);
        return (date.AddDays(1), BatchSlot.AM09);
    }

    public static DateTime CutoffKst(DateOnly date, BatchSlot slot) =>
        date.ToDateTime(slot == BatchSlot.AM09 ? AmCutoff : PmCutoff);

    public static string SlotLabel(BatchSlot slot) => slot == BatchSlot.AM09 ? "09:00" : "15:00";
}

public static class Transitions
{
    static readonly Dictionary<OrderStatus, OrderStatus[]> Order = new()
    {
        [OrderStatus.PaymentPending] = new[] { OrderStatus.Paid, OrderStatus.PaymentFailed },
        [OrderStatus.Paid] = new[] { OrderStatus.InBatch, OrderStatus.Cancelled },
        [OrderStatus.InBatch] = new[] { OrderStatus.Confirmed, OrderStatus.Cancelled, OrderStatus.Refunded },
        [OrderStatus.Confirmed] = new[] { OrderStatus.Preparing, OrderStatus.ReadyForPickup },
        [OrderStatus.Preparing] = new[] { OrderStatus.ReadyForPickup },
        [OrderStatus.ReadyForPickup] = new[] { OrderStatus.PickedUp },
        [OrderStatus.PickedUp] = new[] { OrderStatus.Delivered },
    };

    static readonly Dictionary<StoreOrderStatus, StoreOrderStatus[]> Store = new()
    {
        [StoreOrderStatus.Confirmed] = new[] { StoreOrderStatus.Preparing, StoreOrderStatus.Ready },
        [StoreOrderStatus.Preparing] = new[] { StoreOrderStatus.Ready },
    };

    static readonly Dictionary<JobStatus, JobStatus[]> Job = new()
    {
        [JobStatus.Open] = new[] { JobStatus.Accepted },
        [JobStatus.Accepted] = new[] { JobStatus.PickedUp },
        [JobStatus.PickedUp] = new[] { JobStatus.Delivered },
    };

    public static bool Can(OrderStatus from, OrderStatus to) => Order.TryGetValue(from, out var n) && n.Contains(to);
    public static bool Can(StoreOrderStatus from, StoreOrderStatus to) => Store.TryGetValue(from, out var n) && n.Contains(to);
    public static bool Can(JobStatus from, JobStatus to) => Job.TryGetValue(from, out var n) && n.Contains(to);
}
