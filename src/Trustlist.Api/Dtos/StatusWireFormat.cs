using Trustlist.Api.Domain;

namespace Trustlist.Api.Dtos;

/// <summary>
/// Wire-format <see cref="EntityStatus"/> mapping. Shared by IssuersController,
/// VerifiersController, WalletProvidersController. Snake-case wire format matches
/// openapi-trustlist-directory.yaml §Status and §RoleStatusResponse.
/// </summary>
internal static class StatusWireFormat
{
    public static string Wire(EntityStatus s) => s switch
    {
        EntityStatus.Applied => "applied",
        EntityStatus.Vetted => "vetted",
        EntityStatus.Valid => "valid",
        EntityStatus.Suspended => "suspended",
        EntityStatus.Withdrawn => "withdrawn",
        EntityStatus.Expired => "expired",
        _ => s.ToString().ToLowerInvariant()
    };

    public static bool TryParse(string? wire, out EntityStatus status)
    {
        switch (wire?.ToLowerInvariant())
        {
            case "applied": status = EntityStatus.Applied; return true;
            case "vetted": status = EntityStatus.Vetted; return true;
            case "valid": status = EntityStatus.Valid; return true;
            case "suspended": status = EntityStatus.Suspended; return true;
            case "withdrawn": status = EntityStatus.Withdrawn; return true;
            case "expired": status = EntityStatus.Expired; return true;
            default: status = default; return false;
        }
    }
}
