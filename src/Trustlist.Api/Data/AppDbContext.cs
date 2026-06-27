using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Trustlist.Api.Domain;

namespace Trustlist.Api.Data;

public class AppDbContext : IdentityDbContext<IdentityUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<TrustlistEntity> TrustlistEntities => Set<TrustlistEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<TrustlistEntity>(e =>
        {
            e.HasIndex(x => x.EntityId).IsUnique();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.CertificateId).HasMaxLength(128);

            // JSON-as-string columns. EF won't try to parse them; DTO layer does.
            e.Property(x => x.TrustAnchorsJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.ClientIdentifiersJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.SupportedCredentialFormatsJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.CredentialTypesJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.WiaAttestationFormatJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.KaAttestationFormatJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.ScopeAllowedJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.IssuerScopeJson).HasColumnType("nvarchar(max)");
        });
    }
}
