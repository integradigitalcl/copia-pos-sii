using GrunflexPOS.Web.Services;
using Xunit;

namespace GrunflexPOS.Web.Tests;

public sealed class ScaleLabelBarcodeParserTests
{
    [Fact]
    public void ParsesWeightLabel_Ean13WithPrefix2000()
    {
        // 2000 + PLU 1234 + 1500g + check → 1.5 kg
        var ok = ScaleLabelBarcodeParser.TryParse(
            "2000123415009", "2000", priceLabels: false, weightLabels: true, out var parsed);

        Assert.True(ok);
        Assert.NotNull(parsed);
        Assert.Equal("peso", parsed.Mode);
        Assert.Equal(1.5m, parsed.QuantityKg);
        Assert.Null(parsed.EmbeddedUnitPrice);
        Assert.Contains("1234", parsed.ProductLookup);
    }

    [Fact]
    public void ParsesPriceLabel_Ean13WithPrefix20()
    {
        // 20 + PLU 00123 + price 01990 + check
        var ok = ScaleLabelBarcodeParser.TryParse(
            "2000123019907", "20", priceLabels: true, weightLabels: false, out var parsed);

        Assert.True(ok);
        Assert.NotNull(parsed);
        Assert.Equal("precio", parsed.Mode);
        Assert.Equal(1m, parsed.QuantityKg);
        Assert.Equal(1990m, parsed.EmbeddedUnitPrice);
    }

    [Fact]
    public void RejectsWhenLabelsDisabled()
    {
        var ok = ScaleLabelBarcodeParser.TryParse(
            "2000123415009", "2000", priceLabels: false, weightLabels: false, out _);
        Assert.False(ok);
    }

    [Fact]
    public void RejectsWrongPrefix()
    {
        var ok = ScaleLabelBarcodeParser.TryParse(
            "2000123415009", "21", priceLabels: false, weightLabels: true, out _);
        Assert.False(ok);
    }
}
