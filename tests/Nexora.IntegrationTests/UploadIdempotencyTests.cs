using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexora.Application.Uploads;
using Nexora.Domain.Uploads;
using Nexora.Infrastructure.Configuration;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

public sealed class UploadIdempotencyTests
{
    [PostgresFact]
    public async Task LostCreateResponseCanBeRecoveredAfterRestartWithoutAnotherReservation()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        var tokens = await host.LoginAsync();
        host.Factory.Services.GetRequiredService<IOptions<UploadOptions>>().Value.MaximumOpenUploadsPerOwner = 1;
        var request = new CreateUploadRequest("retry.bin", 16, ClientRequestId: Guid.NewGuid());
        var original = await CreateAsync(host, tokens.AccessToken, request);
        await host.RestartAsync();
        // The test host deliberately uses fresh ephemeral authentication keys on restart.
        // Recover through a new owner session, as another device may retry the same request.
        tokens = await host.LoginAsync();
        host.Factory.Services.GetRequiredService<IOptions<UploadOptions>>().Value.MaximumOpenUploadsPerOwner = 1;
        var recovered = await CreateAsync(host, tokens.AccessToken, request);
        Assert.Equal(original.Id, recovered.Id);
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.UploadSessions.CountAsync());
        Assert.Equal(32, await db.UploadSessions.SumAsync(upload => upload.ReservedBytes));
    }

    [PostgresFact]
    public async Task ConcurrentCreateRetriesShareOneSession()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        var tokens = await host.LoginAsync();
        var request = new CreateUploadRequest("concurrent.bin", 16, ClientRequestId: Guid.NewGuid());
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CreateAsync(host, tokens.AccessToken, request)));
        Assert.Single(results.Select(upload => upload.Id).Distinct());
        await using var scope = host.Factory.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().UploadSessions.CountAsync());
    }

    [PostgresFact]
    public async Task KeyIsOwnerScopedAndDifferentPayloadIsRefused()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        var tokens = await host.LoginAsync();
        var second = await host.CreateSecondOwnerAsync();
        var request = new CreateUploadRequest("shared-key.bin", 16, ClientRequestId: Guid.NewGuid());
        var original = await CreateAsync(host, tokens.AccessToken, request);
        var other = await CreateAsync(host, second.Tokens.AccessToken, request);
        Assert.NotEqual(original.Id, other.Id);
        using var conflict = await host.SendAsync(HttpMethod.Post, "/api/uploads", tokens.AccessToken,
            JsonContent.Create(request with { ExpectedLength = 17 }));
        await AuthenticationTestHost.AssertProblemAsync(conflict, HttpStatusCode.Conflict, "upload_request_conflict");
    }

    [PostgresFact]
    public async Task TerminalCreateReplayPreservesCancellationAndDoesNotStartAnotherUpload()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        var tokens = await host.LoginAsync();
        var request = new CreateUploadRequest("cancelled.bin", 16, ClientRequestId: Guid.NewGuid());
        var original = await CreateAsync(host, tokens.AccessToken, request);
        using var cancellation = await host.SendAsync(HttpMethod.Delete, $"/api/uploads/{original.Id}", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, cancellation.StatusCode);
        var repeated = await CreateAsync(host, tokens.AccessToken, request);
        Assert.Equal(original.Id, repeated.Id);
        Assert.Equal(UploadState.Cancelled, repeated.State);
    }

    [PostgresFact]
    public async Task EmptyClientRequestIdIsRefused()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        var tokens = await host.LoginAsync();
        using var response = await host.SendAsync(HttpMethod.Post, "/api/uploads", tokens.AccessToken,
            JsonContent.Create(new CreateUploadRequest("invalid.bin", 16, ClientRequestId: Guid.Empty)));
        await AuthenticationTestHost.AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    private static async Task<UploadSnapshot> CreateAsync(FileApiTestHost host, string accessToken, CreateUploadRequest request)
    {
        using var response = await host.SendAsync(HttpMethod.Post, "/api/uploads", accessToken, JsonContent.Create(request));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<UploadSnapshot>() ?? throw new InvalidOperationException("Missing upload response.");
    }
}
