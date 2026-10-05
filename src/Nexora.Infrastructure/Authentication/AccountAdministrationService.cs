using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Authentication;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Authentication;

internal sealed class AccountAdministrationService(
    NexoraDbContext db,
    UserManager<NexoraUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    TimeProvider clock,
    ILogger<AccountAdministrationService> logger) : IAccountAdministration
{
    private const long BootstrapAdvisoryLock = 0x4E45584F5241;
    private static readonly EventId Bootstrapped = new(2101, "AdministratorBootstrapped");
    private static readonly EventId PasswordReset = new(2102, "AdministratorPasswordReset");
    private static readonly EventId Rejected = new(2103, "AccountAdministrationRejected");

    public async Task<AuthenticationResult<Guid>> BootstrapAsync(string email, string password, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BootstrapAdvisoryLock})", cancellationToken);
        if (await db.Users.AnyAsync(cancellationToken))
            return AuthenticationResult<Guid>.Fail(AuthenticationFailure.AlreadyInitialized);

        if (string.IsNullOrWhiteSpace(email) || email.Length > 256 || password.Length is < 14 or > 256)
            return Reject();
        var now = clock.GetUtcNow();
        var user = new NexoraUser
        {
            Id = Guid.CreateVersion7(now), UserName = email.Trim(), Email = email.Trim(), LockoutEnabled = true
        };
        cancellationToken.ThrowIfCancellationRequested();
        if (!(await users.CreateAsync(user, password)).Succeeded) return Reject();

        if (!await roles.RoleExistsAsync(AuthenticationConstants.AdministratorRole))
        {
            var role = new IdentityRole<Guid>(AuthenticationConstants.AdministratorRole) { Id = Guid.CreateVersion7(now) };
            if (!(await roles.CreateAsync(role)).Succeeded) return Reject();
        }
        if (!(await users.AddToRoleAsync(user, AuthenticationConstants.AdministratorRole)).Succeeded) return Reject();
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(Bootstrapped, "Administrator {UserId} created.", user.Id);
        return AuthenticationResult<Guid>.Success(user.Id);
    }

    public async Task<AuthenticationResult<Guid>> ResetAdministratorPasswordAsync(string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password) || password.Length is < 14 or > 256) return Reject();
        var normalizedRole = roles.NormalizeKey(AuthenticationConstants.AdministratorRole);
        var administrators = await db.UserRoles.AsNoTracking().Join(db.Roles.AsNoTracking(),
                userRole => userRole.RoleId, role => role.Id, (userRole, role) => new { userRole.UserId, role.NormalizedName })
            .Where(item => item.NormalizedName == normalizedRole).Select(item => item.UserId).Take(2).ToListAsync(cancellationToken);
        if (administrators.Count != 1) return AuthenticationResult<Guid>.Fail(AuthenticationFailure.AdministratorNotFound);

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var user = await UserTransactionLock.AcquireAsync(db, administrators[0], cancellationToken);
        if (user is null || !await users.IsInRoleAsync(user, AuthenticationConstants.AdministratorRole))
            return AuthenticationResult<Guid>.Fail(AuthenticationFailure.AdministratorNotFound);

        // Both native Identity operations are inside the same transaction. Rejected passwords
        // roll back the removal; no reset token or recovery endpoint is exposed over HTTP.
        if (!(await users.RemovePasswordAsync(user)).Succeeded || !(await users.AddPasswordAsync(user, password)).Succeeded)
            return Reject();
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        user.LockoutEnabled = true;
        if (!(await users.UpdateAsync(user)).Succeeded) return Reject();
        var now = clock.GetUtcNow();
        await db.AuthSessions.Where(session => session.UserId == user.Id && session.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAt, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(PasswordReset, "Password reset and sessions revoked for administrator {UserId}.", user.Id);
        return AuthenticationResult<Guid>.Success(user.Id);
    }

    private AuthenticationResult<Guid> Reject()
    {
        logger.LogWarning(Rejected, "Account administration rejected by Identity validation or persistence checks.");
        return AuthenticationResult<Guid>.Fail(AuthenticationFailure.PasswordRejected);
    }
}
