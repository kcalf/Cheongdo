using Api.Services;
using Domain;
using Xunit;

namespace Api.Tests;

public class KeywordInterpreterTests
{
    static readonly List<Product> Products =
    [
        new Product { Id = 1, Name = "두부 1모", Keywords = "두부" },
        new Product { Id = 2, Name = "대파 1단", Keywords = "대파" },
        new Product { Id = 3, Name = "우유 1L", Keywords = "우유" },
    ];

    static Task<List<InterpretedItem>> Run(string s) => new KeywordInterpreter().InterpretAsync(s, Products, CancellationToken.None);

    [Theory]
    [InlineData("두부 2모랑 대파 한 단")]
    [InlineData("두부 2모와 대파 한 단")]
    [InlineData("두부 2모, 대파 한 단")]
    [InlineData("두부 2모 그리고 대파 한 단")]
    public async Task Splits_items_on_particles(string text)
    {
        var r = await Run(text);
        Assert.Equal([1, 2], r.Select(i => i.ProductId));
        Assert.Equal([2, 1], r.Select(i => i.Qty));
    }

    [Fact]
    public async Task Keeps_unknown_name_ending_in_gwa_whole()
    {
        var r = await Run("사과 3개, 우유 2개");
        Assert.Equal(2, r.Count);
        Assert.Equal("notfound", r[0].Status);
        Assert.Equal(3, r[0].Qty);
        Assert.Equal(3, r[1].ProductId);
    }
}
