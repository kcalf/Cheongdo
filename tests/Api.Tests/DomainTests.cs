using Domain;
using Xunit;

namespace Api.Tests;

public class DomainTests
{
    [Theory]
    [InlineData(3500, 1, new[] { 3500 })]
    [InlineData(4000, 3, new[] { 1334, 1333, 1333 })]
    [InlineData(5000, 2, new[] { 2500, 2500 })]
    public void Split_keeps_total(int fee, int stops, int[] expected)
    {
        var s = Fees.Split(fee, stops);
        Assert.Equal(expected, s);
        Assert.Equal(fee, s.Sum());
    }

    [Fact] public void JobFee_is_base_plus_stops() => Assert.Equal(4500, Fees.JobFee(3000, 500, 3));

    [Theory]
    [InlineData("2026-11-08", false)] // 2번째 일요일
    [InlineData("2026-11-22", false)] // 4번째 일요일
    [InlineData("2026-11-01", true)]
    [InlineData("2026-11-15", true)]
    [InlineData("2026-11-09", true)]
    public void Top_mart_closed_on_2nd_and_4th_sunday(string d, bool open) =>
        Assert.Equal(open, StoreCalendar.IsOpen(StoreCalendar.Sunday2And4, DateOnly.Parse(d)));

    [Fact] public void Other_stores_always_open() => Assert.True(StoreCalendar.IsOpen(StoreCalendar.None, new DateOnly(2026, 11, 8)));

    [Theory]
    [InlineData("2026-11-09T08:29:00", "2026-11-09", BatchSlot.AM09)]
    [InlineData("2026-11-09T08:30:00", "2026-11-09", BatchSlot.PM15)]
    [InlineData("2026-11-09T14:30:00", "2026-11-10", BatchSlot.AM09)]
    public void Next_batch(string now, string date, BatchSlot slot) =>
        Assert.Equal((DateOnly.Parse(date), slot), BatchSchedule.Next(DateTime.Parse(now)));

    [Fact]
    public void Transitions_block_skips()
    {
        Assert.True(Transitions.Can(OrderStatus.InBatch, OrderStatus.Confirmed));
        Assert.False(Transitions.Can(OrderStatus.Delivered, OrderStatus.Cancelled));
        Assert.False(Transitions.Can(JobStatus.Open, JobStatus.Delivered));
        Assert.True(Transitions.Can(StoreOrderStatus.Preparing, StoreOrderStatus.Ready));
    }
}
