namespace GrunflexPOS.Web.Services;

public static class InventoryStockRules
{
    public static decimal GetEffectiveMinStock(decimal productMinStock, decimal globalThreshold) =>
        productMinStock > 0 ? productMinStock : globalThreshold;

    public static bool IsLowStock(decimal stock, decimal productMinStock, decimal globalThreshold)
    {
        var threshold = GetEffectiveMinStock(productMinStock, globalThreshold);
        return threshold > 0 ? stock <= threshold : stock <= 0;
    }
}
