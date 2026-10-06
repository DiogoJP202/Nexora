namespace Nexora.Application.Storage;

public readonly record struct TemporaryObjectKey
{
    public TemporaryObjectKey(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A temporary object needs a nonempty identifier.", nameof(id));
        Id = id;
    }

    public Guid Id { get; }

    public static TemporaryObjectKey Parse(string value)
    {
        if (value is null || value.Length != 32 || !Guid.TryParseExact(value, "N", out var id)
            || id == Guid.Empty || id.ToString("N") != value)
        {
            throw new ArgumentException("Invalid temporary object key.", nameof(value));
        }
        return new TemporaryObjectKey(id);
    }

    public override string ToString()
    {
        if (Id == Guid.Empty) throw new ArgumentException("Invalid temporary object key.");
        return Id.ToString("N");
    }
}
