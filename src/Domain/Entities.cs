namespace Domain;

public enum OrderStatus { PaymentPending, PaymentFailed, Paid, InBatch, Confirmed, Preparing, ReadyForPickup, PickedUp, Delivered, Cancelled, Refunded }
public enum StoreOrderStatus { Confirmed, Preparing, Ready }
public enum ItemStatus { Active, Refunded }
public enum BatchStatus { Open, Confirming, Confirmed, Delivered, Failed }
public enum BatchSlot { AM09, PM15 }
public enum JobStatus { Open, Accepted, PickedUp, Delivered }
public enum PaymentKind { Authorize, Refund, Additional }
public enum PaymentStatus { Authorized, Pending, Settled }
public enum AgentRunStatus { Running, Completed, Failed, Cancelled }

public class Zone
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class ZoneDistance
{
    public int FromZoneId { get; set; }
    public int ToZoneId { get; set; }
    public int DistanceM { get; set; }
}

public class Resident
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = "";
    public int ZoneId { get; set; }
    public string AddressLabel { get; set; } = "";
}

public class Courier
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = "";
}

public class Store
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int ZoneId { get; set; }
    public TimeOnly OpenTime { get; set; }
    public TimeOnly CloseTime { get; set; }
    public string ClosedRule { get; set; } = StoreCalendar.None;
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Category { get; set; } = "";
    public string Keywords { get; set; } = "";
}

public class StoreProduct
{
    public int StoreId { get; set; }
    public int ProductId { get; set; }
    public int Price { get; set; }
    public int StockQty { get; set; }
}

public class Batch
{
    public int Id { get; set; }
    public DateOnly BusinessDate { get; set; }
    public BatchSlot Slot { get; set; }
    public DateTime CutoffAt { get; set; }
    public BatchStatus Status { get; set; }
    public DateTime? ConfirmedAt { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public int ResidentId { get; set; }
    public int ZoneId { get; set; }
    public int? BatchId { get; set; }
    public int? DeliveryJobId { get; set; }
    public OrderStatus Status { get; set; }
    public int EstimatedTotal { get; set; }
    public int? FinalTotal { get; set; }
    public int? FeeShare { get; set; }
    public int? IndividualBaselineTotal { get; set; }
    public int? Savings { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<OrderItem> Items { get; set; } = new();
    public List<Payment> Payments { get; set; } = new();
}

public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public int? PreferredStoreId { get; set; }
    public int? StoreId { get; set; }
    public int? StoreOrderId { get; set; }
    public int Qty { get; set; }
    public int EstimatedUnitPrice { get; set; }
    public int? FinalUnitPrice { get; set; }
    public ItemStatus Status { get; set; }
    public string Source { get; set; } = "Manual";
    public string? StoreChangedReason { get; set; }
}

public class StoreOrder
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int StoreId { get; set; }
    public int? DeliveryJobId { get; set; }
    public StoreOrderStatus Status { get; set; }
}

public class Payment
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int? OrderItemId { get; set; }
    public PaymentKind Kind { get; set; }
    public int Amount { get; set; }
    public PaymentStatus Status { get; set; }
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class DeliveryJob
{
    public int Id { get; set; }
    public int BatchId { get; set; }
    public int ZoneId { get; set; }
    public JobStatus Status { get; set; }
    public int? AcceptedByCourierId { get; set; }
    public int StopCount { get; set; }
    public int BaseFee { get; set; }
    public int PerStopFee { get; set; }
    public int EstimatedDistanceM { get; set; }
    public int EstimatedEarning { get; set; }
    public string RouteOrderJson { get; set; } = "[]";
}

public class AgentRun
{
    public int Id { get; set; }
    public int ResidentId { get; set; }
    public AgentRunStatus Status { get; set; }
    public string? InputText { get; set; }
    public string? ResultJson { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AgentEvent
{
    public int RunId { get; set; }
    public int EventId { get; set; }
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}

public class IdempotencyRecord
{
    public string Key { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string? ResponseJson { get; set; }
    public int? ResponseStatus { get; set; }
    public bool Done { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AuditLog
{
    public int Id { get; set; }
    public string ActorRole { get; set; } = "";
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string DetailJson { get; set; } = "{}";
    public string TraceId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
