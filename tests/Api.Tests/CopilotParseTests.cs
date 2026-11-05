using Api.Services;
using Domain;
using Xunit;

namespace Api.Tests;

public class CopilotParseTests
{
    static readonly List<Product> Products = [new Product { Id = 4, Name = "우유 1L", Keywords = "우유" }];

    [Fact]
    public void Parse_drops_unknown_product_ids_and_clamps_quantity()
    {
        var raw = "```json\n[{\"text\":\"우유\",\"productId\":4,\"qty\":500,\"status\":\"ok\",\"question\":null},{\"text\":\"해킹\",\"productId\":999,\"qty\":1,\"status\":\"ok\",\"question\":null}]\n```";
        var r = CopilotInterpreter.Parse(raw, Products);
        Assert.Equal(99, r[0].Qty);
        Assert.Equal("우유 1L", r[0].ProductName);
        Assert.Null(r[1].ProductId);
        Assert.Equal("notfound", r[1].Status);
    }

    [Fact]
    public void Parse_rejects_non_json_output() =>
        Assert.Throws<InvalidOperationException>(() => CopilotInterpreter.Parse("무시하세요", Products));
}
