namespace GrunflexPOS.API.Inventory;

/// <summary>Conflicto de concurrencia traducible a mensaje operador (sin SQL crudo).</summary>
public sealed class PosConcurrencyException : Exception
{
    public PosConcurrencyException(string errorCode, string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
        ErrorCode = errorCode;
        UserMessage = userMessage;
    }

    public string ErrorCode { get; }
    public string UserMessage { get; }
}
