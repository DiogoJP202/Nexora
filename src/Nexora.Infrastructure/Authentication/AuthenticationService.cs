using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Application.Authentication;
using Nexora.Domain.Authentication;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Persistence;
using IAuthenticationService = Nexora.Application.Authentication.IAuthenticationService;

namespace Nexora.Infrastructure.Authentication;

internal sealed class AuthenticationService(
    NexoraDbContext db,
    UserManager<NexoraUser> users,
    SignInManager<NexoraUser> signIn,
    IUserClaimsPrincipalFactory<NexoraUser> principals,
    IOptionsMonitor<BearerTokenOptions> bearerOptions,
    TimeProvider clock,
    DummyPasswordVerifier dummyPassword,
    ILogger<AuthenticationService> logger) : IAuthenticationService
{
    private static readonly TimeSpan LastSeenInterval = TimeSpan.FromMinutes(5);
    private static readonly EventId LoggedIn = new(2001, "UserLoggedIn");
    private static readonly EventId LoginFailed = new(2002, "UserLoginFailed");
    private static readonly EventId DeviceRegistered = new(2003, "DeviceRegistered");
    private static readonly EventId TokenRotated = new(2004, "RefreshTokenRotated");
    private static readonly EventId ReplayDetected = new(2005, "RefreshTokenReplayDetected");
    private static readonly EventId DeviceRevoked = new(2006, "DeviceRevoked");
    private static readonly EventId SessionRevoked = new(2007, "SessionRevoked");

    public async Task<AuthenticationResult<AuthenticationTokens>> LoginAsync(LoginCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Login) || command.Login.Length > 256 ||
            string.IsNullOrEmpty(command.Password) || command.Password.Length > 256)
            return AuthenticationResult<AuthenticationTokens>.Fail(AuthenticationFailure.InvalidCredentials);

        var normalizedName = users.NormalizeName(command.Login.Trim());
        var normalizedEmail = users.NormalizeEmail(command.Login.Trim());
        var userId = await db.Users.AsNoTracking()
            .Where(user => user.NormalizedUserName == normalizedName || user.NormalizedEmail == normalizedEmail)
            .Select(user => (Guid?)user.Id).SingleOrDefaultAsync(cancellationToken);

        if (userId is null)
        {
            dummyPassword.Verify(command.Password);
            logger.LogInformation(LoginFailed, "Login rejected with {Outcome}.", "invalid_credentials");
            return AuthenticationResult<AuthenticationTokens>.Fail(AuthenticationFailure.InvalidCredentials);
        }

        try
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var user = await UserTransactionLock.AcquireAsync(db, userId.Value, cancellationToken);
            var now = clock.GetUtcNow();
            if (user is null || IsLocked(user, now) || user.TwoFactorEnabled)
            {
                dummyPassword.Verify(command.Password);
                return RejectLogin(userId.Value);
            }

            // Identity uses its own system clock. Clear expired lockout under the user lock
            // so the application's injected clock remains authoritative for session and lockout expiry.
            if (user.LockoutEnd is not null)
            {
                user.LockoutEnd = null;
                user.AccessFailedCount = 0;
                if (!(await users.UpdateAsync(user)).Succeeded) return RejectLogin(user.Id);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var passwordResult = await signIn.CheckPasswordSignInAsync(user, command.Password, lockoutOnFailure: true);
            cancellationToken.ThrowIfCancellationRequested();
            // Identity converts concurrency failures to generic sign-in failures. Never retry or
            // accidentally commit the still-modified entity after such a failed store update.
            if (db.Entry(user).State != EntityState.Unchanged) return RejectLogin(user.Id);

            if (!passwordResult.Succeeded)
            {
                if (passwordResult.IsLockedOut)
                {
                    user.LockoutEnd = now + signIn.Options.Lockout.DefaultLockoutTimeSpan;
                    if (!(await users.UpdateAsync(user)).Succeeded) return RejectLogin(user.Id);
                }
                await transaction.CommitAsync(cancellationToken);
                return RejectLogin(user.Id);
            }

            Device? device;
            if (command.DeviceId is { } deviceId)
            {
                device = await db.Devices.SingleOrDefaultAsync(item => item.Id == deviceId && item.UserId == user.Id, cancellationToken);
                if (device is null || device.RevokedAt is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return AuthenticationResult<AuthenticationTokens>.Fail(AuthenticationFailure.InvalidDevice);
                }
                await RevokeSessionsAsync(user.Id, device.Id, now, cancellationToken);
            }
            else
            {
                device = new Device(Guid.CreateVersion7(now), user.Id, command.DeviceName.Trim(), command.Platform.Trim(), now);
                db.Devices.Add(device);
            }

            var session = new AuthSession(Guid.CreateVersion7(now), user.Id, device.Id,
                user.SecurityStamp!, now, now + AuthenticationConstants.SessionLifetime);
            db.AuthSessions.Add(session);
            var (refresh, rawToken) = CreateRefreshToken(session, now);
            db.RefreshTokens.Add(refresh);
            var tokens = await IssueTokensAsync(user, session, rawToken, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (command.DeviceId is null)
                logger.LogInformation(DeviceRegistered, "Device {DeviceId} registered for user {UserId}.", device.Id, user.Id);
            logger.LogInformation(LoggedIn, "User {UserId} authenticated with session {SessionId} and device {DeviceId}.", user.Id, session.Id, device.Id);
            return AuthenticationResult<AuthenticationTokens>.Success(tokens);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return RejectLogin(userId.Value);
        }
    }

    public async Task<AuthenticationResult<AuthenticationTokens>> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = GetRefreshHash(refreshToken);
        if (hash is null) return InvalidRefresh();
        var existing = await db.RefreshTokens.AsNoTracking().Where(token => token.TokenHash == hash)
            .Select(token => new { token.Id, token.Session.UserId }).SingleOrDefaultAsync(cancellationToken);
        if (existing is null) return InvalidRefresh();

        try
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var user = await UserTransactionLock.AcquireAsync(db, existing.UserId, cancellationToken);
            var token = await db.RefreshTokens.Include(item => item.Session).ThenInclude(session => session.Device)
                .SingleOrDefaultAsync(item => item.Id == existing.Id && item.TokenHash == hash, cancellationToken);
            if (user is null || token is null) return InvalidRefresh();
            var now = clock.GetUtcNow();
            var session = token.Session;

            if (token.UsedAt is not null)
            {
                session.Revoke(now);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                logger.LogWarning(ReplayDetected, "Refresh replay revoked session {SessionId} for user {UserId}.", session.Id, user.Id);
                return InvalidRefresh();
            }

            if (token.ExpiresAt <= now || session.ExpiresAt <= now || session.RevokedAt is not null ||
                session.Device.RevokedAt is not null || session.UserId != user.Id || session.Device.UserId != user.Id ||
                session.SecurityStamp != user.SecurityStamp || IsLocked(user, now) || user.TwoFactorEnabled)
                return InvalidRefresh();

            var (successor, rawToken) = CreateRefreshToken(session, now);
            token.Consume(now, successor.Id);
            db.RefreshTokens.Add(successor);
            var tokens = await IssueTokensAsync(user, session, rawToken, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(TokenRotated, "Refresh rotated for user {UserId}, session {SessionId}.", user.Id, session.Id);
            return AuthenticationResult<AuthenticationTokens>.Success(tokens);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(TokenRotated, "Refresh rejected due to a persistence conflict.");
            return InvalidRefresh();
        }
    }

    public async Task LogoutAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (await UserTransactionLock.AcquireAsync(db, userId, cancellationToken) is null) return;
        var session = await db.AuthSessions.SingleOrDefaultAsync(item => item.Id == sessionId && item.UserId == userId, cancellationToken);
        if (session is null) return;
        session.Revoke(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(SessionRevoked, "Session {SessionId} revoked for user {UserId}.", sessionId, userId);
    }

    public async Task<IReadOnlyList<DeviceSummary>> ListDevicesAsync(Guid userId, CancellationToken cancellationToken)
        => await db.Devices.AsNoTracking().Where(device => device.UserId == userId)
            .OrderByDescending(device => device.CreatedAt).ThenBy(device => device.Id)
            .Select(device => new DeviceSummary(device.Id, device.Name, device.Platform,
                device.CreatedAt, device.LastSeenAt, device.RevokedAt)).ToListAsync(cancellationToken);

    public async Task<bool> RevokeDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (await UserTransactionLock.AcquireAsync(db, userId, cancellationToken) is null) return false;
        var device = await db.Devices.SingleOrDefaultAsync(item => item.Id == deviceId && item.UserId == userId, cancellationToken);
        if (device is null) return false;
        var now = clock.GetUtcNow();
        device.Revoke(now);
        await db.SaveChangesAsync(cancellationToken);
        await RevokeSessionsAsync(userId, deviceId, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(DeviceRevoked, "Device {DeviceId} revoked for user {UserId}.", deviceId, userId);
        return true;
    }

    public async Task<bool> ValidateSessionAsync(Guid userId, Guid sessionId, Guid deviceId, string securityStamp, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var lastSeen = await ValidSessions(userId, sessionId, deviceId, securityStamp, now)
            .Select(session => (DateTimeOffset?)session.Device.LastSeenAt).SingleOrDefaultAsync(cancellationToken);
        if (lastSeen is null) return false;
        if (lastSeen > now - LastSeenInterval) return true;

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (await UserTransactionLock.AcquireAsync(db, userId, cancellationToken) is null ||
            !await ValidSessions(userId, sessionId, deviceId, securityStamp, now).AnyAsync(cancellationToken)) return false;
        await db.Devices.Where(device => device.UserId == userId && device.Id == deviceId && device.RevokedAt == null &&
                device.LastSeenAt <= now - LastSeenInterval && device.LastSeenAt < now)
            .ExecuteUpdateAsync(update => update.SetProperty(device => device.LastSeenAt, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private IQueryable<AuthSession> ValidSessions(Guid userId, Guid sessionId, Guid deviceId, string stamp, DateTimeOffset now)
        => db.AuthSessions.AsNoTracking().Where(session => session.Id == sessionId && session.UserId == userId &&
            session.DeviceId == deviceId && session.Device.UserId == userId && session.RevokedAt == null &&
            session.Device.RevokedAt == null && session.ExpiresAt > now && session.SecurityStamp == stamp)
            .Join(db.Users.AsNoTracking().Where(user => user.Id == userId && user.SecurityStamp == stamp && !user.TwoFactorEnabled &&
                    (!user.LockoutEnabled || user.LockoutEnd == null || user.LockoutEnd <= now)),
                session => session.UserId, user => user.Id, (session, _) => session);

    private Task<int> RevokeSessionsAsync(Guid userId, Guid deviceId, DateTimeOffset now, CancellationToken cancellationToken)
        => db.AuthSessions.Where(session => session.UserId == userId && session.DeviceId == deviceId && session.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAt, now), cancellationToken);

    private async Task<AuthenticationTokens> IssueTokensAsync(NexoraUser user, AuthSession session,
        string refreshToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var principal = await principals.CreateAsync(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(AuthenticationConstants.SessionIdClaim, session.Id.ToString()));
        identity.AddClaim(new Claim(AuthenticationConstants.DeviceIdClaim, session.DeviceId.ToString()));
        var expiresAt = now + AuthenticationConstants.AccessTokenLifetime;
        if (expiresAt > session.ExpiresAt) expiresAt = session.ExpiresAt;
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = expiresAt },
            $"{AuthenticationConstants.Scheme}:AccessToken");
        var accessToken = bearerOptions.Get(AuthenticationConstants.Scheme).BearerTokenProtector.Protect(ticket);
        return new AuthenticationTokens("Bearer", accessToken, (long)(expiresAt - now).TotalSeconds,
            refreshToken, session.DeviceId, session.Id, session.ExpiresAt);
    }

    private static (RefreshToken Token, string RawToken) CreateRefreshToken(AuthSession session, DateTimeOffset now)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = new RefreshToken(Guid.CreateVersion7(now), session.Id, SHA256.HashData(bytes), now, session.ExpiresAt);
        return (token, WebEncoders.Base64UrlEncode(bytes));
    }

    private static byte[]? GetRefreshHash(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 43) return null;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(value);
            return bytes.Length == 32 && WebEncoders.Base64UrlEncode(bytes) == value ? SHA256.HashData(bytes) : null;
        }
        catch (FormatException) { return null; }
    }

    private AuthenticationResult<AuthenticationTokens> RejectLogin(Guid userId)
    {
        logger.LogInformation(LoginFailed, "Login rejected for user {UserId}.", userId);
        return AuthenticationResult<AuthenticationTokens>.Fail(AuthenticationFailure.InvalidCredentials);
    }

    private static AuthenticationResult<AuthenticationTokens> InvalidRefresh()
        => AuthenticationResult<AuthenticationTokens>.Fail(AuthenticationFailure.InvalidRefreshToken);

    private static bool IsLocked(NexoraUser user, DateTimeOffset now)
        => user.LockoutEnabled && user.LockoutEnd > now;
}
