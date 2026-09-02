using GrunflexPOS.Web.Services;
using Xunit;

namespace GrunflexPOS.Web.Tests;

public sealed class InventoryStockRulesTests
{
    [Theory]
    [InlineData(0, 5, 5)]
    [InlineData(10, 5, 10)]
    public void GetEffectiveMinStock_UsesProductOrGlobal(decimal productMin, decimal global, decimal expected) =>
        Assert.Equal(expected, InventoryStockRules.GetEffectiveMinStock(productMin, global));

    [Theory]
    [InlineData(3, 0, 5, true)]
    [InlineData(6, 0, 5, false)]
    [InlineData(8, 10, 5, true)]
    [InlineData(11, 10, 5, false)]
    [InlineData(0, 0, 0, true)]
    [InlineData(2, 0, 0, false)]
    public void IsLowStock_RespectsThreshold(decimal stock, decimal productMin, decimal global, bool expected) =>
        Assert.Equal(expected, InventoryStockRules.IsLowStock(stock, productMin, global));
}
