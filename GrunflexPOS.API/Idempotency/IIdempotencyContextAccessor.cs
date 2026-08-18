namespace GrunflexPOS.API.Idempotency;

public interface IIdempotencyContextAccessor
{
    IdempotencyRequestDescriptor? Current { get; }
    void Set(IdempotencyRequestDescriptor descriptor);
}

public sealed class IdempotencyContextAccessor : IIdempotencyContextAccessor
{
    private IdempotencyRequestDescriptor? _current;

    public IdempotencyRequestDescriptor? Current => _current;

    public void Set(IdempotencyRequestDescriptor descriptor) => _current = descriptor;
}
