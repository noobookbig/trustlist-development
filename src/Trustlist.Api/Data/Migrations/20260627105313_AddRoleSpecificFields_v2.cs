using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trustlist.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleSpecificFields_v2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CertificationExpiresAt",
                table: "TrustlistEntities",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CertificationIssuedAt",
                table: "TrustlistEntities",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificationIssuingBody",
                table: "TrustlistEntities",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificationSchemeVersion",
                table: "TrustlistEntities",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientIdentifiersJson",
                table: "TrustlistEntities",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CredentialTypesJson",
                table: "TrustlistEntities",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CsirtEmail",
                table: "TrustlistEntities",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuerScopeJson",
                table: "TrustlistEntities",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KaAttestationFormatJson",
                table: "TrustlistEntities",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScopeAllowedJson",
                table: "TrustlistEntities",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusListEndpoint",
                table: "TrustlistEntities",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupportedCredentialFormatsJson",
                table: "TrustlistEntities",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrustAnchorsJson",
                table: "TrustlistEntities",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WalletUnitAuditLogUri",
                table: "TrustlistEntities",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WiaAttestationFormatJson",
                table: "TrustlistEntities",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WiaRevocationMaintenancePeriodDays",
                table: "TrustlistEntities",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WiaStatusListUri",
                table: "TrustlistEntities",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CertificationExpiresAt",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "CertificationIssuedAt",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "CertificationIssuingBody",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "CertificationSchemeVersion",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "ClientIdentifiersJson",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "CredentialTypesJson",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "CsirtEmail",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "IssuerScopeJson",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "KaAttestationFormatJson",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "ScopeAllowedJson",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "StatusListEndpoint",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "SupportedCredentialFormatsJson",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "TrustAnchorsJson",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "WalletUnitAuditLogUri",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "WiaAttestationFormatJson",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "WiaRevocationMaintenancePeriodDays",
                table: "TrustlistEntities");

            migrationBuilder.DropColumn(
                name: "WiaStatusListUri",
                table: "TrustlistEntities");
        }
    }
}
