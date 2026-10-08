using Nexora.Application.Jobs;
using Nexora.Application.Storage;

namespace Nexora.Application.Images;

public sealed class ImageJobProcessor(IImageWorkStore store, IBlobStorage originals,
    IImageRenderer renderer, IDerivativeStorage derivatives) : IImageJobProcessor
{
    public async Task ProcessAsync(ImageWorkItem work, CancellationToken cancellationToken)
    {
        foreach (var attempt in work.PreviousAttempts)
            await derivatives.CleanAttemptAsync(attempt, preservePublished: false, cancellationToken);
        await using var original = await originals.OpenReadAsync(work.Blob.StorageKey, cancellationToken);
        var image = await renderer.RenderAsync(work.Blob, original, cancellationToken);
        var thumbnail = await derivatives.PublishAsync(new DerivativeKey(work.Lease.Token, DerivativeKind.Thumbnail), image.Thumbnail, cancellationToken);
        var preview = await derivatives.PublishAsync(new DerivativeKey(work.Lease.Token, DerivativeKind.Preview), image.Preview, cancellationToken);
        // If a commit response is lost, leave this generation intact until durable
        // cleanup decides whether it is the published result or an abandoned attempt.
        if (!await store.CompleteAsync(work.Lease, image, thumbnail, preview, cancellationToken))
            throw new JobLeaseLostException();
    }
}
