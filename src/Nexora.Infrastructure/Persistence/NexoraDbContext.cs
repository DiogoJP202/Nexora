using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Nexora.Infrastructure.Identity;

namespace Nexora.Infrastructure.Persistence;

public sealed class NexoraDbContext(DbContextOptions<NexoraDbContext> options)
    : IdentityDbContext<NexoraUser, IdentityRole<Guid>, Guid>(options);
