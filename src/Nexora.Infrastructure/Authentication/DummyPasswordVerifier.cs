using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Nexora.Infrastructure.Identity;

namespace Nexora.Infrastructure.Authentication;

internal sealed class DummyPasswordVerifier
{
    private readonly NexoraUser _user = new();
    private readonly PasswordHasher<NexoraUser> _hasher;
    private readonly string _hash;

    public DummyPasswordVerifier(IOptions<PasswordHasherOptions> options)
    {
        _hasher = new PasswordHasher<NexoraUser>(options);
        _hash = _hasher.HashPassword(_user, Guid.NewGuid().ToString("N"));
    }

    public void Verify(string password) => _hasher.VerifyHashedPassword(_user, _hash, password);
}
