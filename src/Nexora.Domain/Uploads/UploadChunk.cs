namespace Nexora.Domain.Uploads;

public sealed class UploadChunk
{
    private UploadChunk() { }

    public UploadChunk(Guid uploadSessionId, int number, long size, string sha256, Guid temporaryId)
    {
        if (uploadSessionId == Guid.Empty || temporaryId == Guid.Empty)
            throw new ArgumentException("A chunk requires valid identifiers.");
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        UploadSession.ValidateHash(sha256);
        UploadSessionId = uploadSessionId;
        Number = number;
        Size = size;
        Sha256 = sha256;
        TemporaryId = temporaryId;
    }

    public Guid UploadSessionId { get; private set; }
    public int Number { get; private set; }
    public long Size { get; private set; }
    public string Sha256 { get; private set; } = string.Empty;
    public Guid TemporaryId { get; private set; }
    public UploadSession Session { get; private set; } = null!;
}
