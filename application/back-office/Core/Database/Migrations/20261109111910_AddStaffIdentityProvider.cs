using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackOffice.Database.Migrations;

/// <summary>OpenIddict's store for the staff OIDC provider: its applications (synced from the committed client list), authorizations, and issued tokens.</summary>
[DbContext(typeof(BackOfficeDbContext))]
[Migration("20261109111910_AddStaffIdentityProvider")]
public sealed class AddStaffIdentityProvider : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            "openiddict_applications",
            table => new
            {
                id = table.Column<string>("text", nullable: false),
                application_type = table.Column<string>("text", nullable: true),
                client_id = table.Column<string>("text", nullable: true),
                client_secret = table.Column<string>("text", nullable: true),
                client_type = table.Column<string>("text", nullable: true),
                concurrency_token = table.Column<string>("text", nullable: true),
                consent_type = table.Column<string>("text", nullable: true),
                display_name = table.Column<string>("text", nullable: true),
                display_names = table.Column<string>("text", nullable: true),
                json_web_key_set = table.Column<string>("text", nullable: true),
                permissions = table.Column<string>("text", nullable: true),
                post_logout_redirect_uris = table.Column<string>("text", nullable: true),
                properties = table.Column<string>("text", nullable: true),
                redirect_uris = table.Column<string>("text", nullable: true),
                requirements = table.Column<string>("text", nullable: true),
                settings = table.Column<string>("text", nullable: true)
            },
            constraints: table => { table.PrimaryKey("pk_openiddict_applications", x => x.id); }
        );

        migrationBuilder.CreateTable(
            "openiddict_scopes",
            table => new
            {
                id = table.Column<string>("text", nullable: false),
                concurrency_token = table.Column<string>("text", nullable: true),
                description = table.Column<string>("text", nullable: true),
                descriptions = table.Column<string>("text", nullable: true),
                display_name = table.Column<string>("text", nullable: true),
                display_names = table.Column<string>("text", nullable: true),
                name = table.Column<string>("text", nullable: true),
                properties = table.Column<string>("text", nullable: true),
                resources = table.Column<string>("text", nullable: true)
            },
            constraints: table => { table.PrimaryKey("pk_openiddict_scopes", x => x.id); }
        );

        migrationBuilder.CreateTable(
            "openiddict_authorizations",
            table => new
            {
                id = table.Column<string>("text", nullable: false),
                application_id = table.Column<string>("text", nullable: true),
                concurrency_token = table.Column<string>("text", nullable: true),
                creation_date = table.Column<DateTimeOffset>("timestamptz", nullable: true),
                properties = table.Column<string>("text", nullable: true),
                scopes = table.Column<string>("text", nullable: true),
                status = table.Column<string>("text", nullable: true),
                subject = table.Column<string>("text", nullable: true),
                type = table.Column<string>("text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_openiddict_authorizations", x => x.id);
                table.ForeignKey("fk_openiddict_authorizations_openiddict_applications_applicati", x => x.application_id, "openiddict_applications", "id");
            }
        );

        migrationBuilder.CreateTable(
            "openiddict_tokens",
            table => new
            {
                id = table.Column<string>("text", nullable: false),
                application_id = table.Column<string>("text", nullable: true),
                authorization_id = table.Column<string>("text", nullable: true),
                concurrency_token = table.Column<string>("text", nullable: true),
                creation_date = table.Column<DateTimeOffset>("timestamptz", nullable: true),
                expiration_date = table.Column<DateTimeOffset>("timestamptz", nullable: true),
                payload = table.Column<string>("text", nullable: true),
                properties = table.Column<string>("text", nullable: true),
                redemption_date = table.Column<DateTimeOffset>("timestamptz", nullable: true),
                reference_id = table.Column<string>("text", nullable: true),
                status = table.Column<string>("text", nullable: true),
                subject = table.Column<string>("text", nullable: true),
                type = table.Column<string>("text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_openiddict_tokens", x => x.id);
                table.ForeignKey("fk_openiddict_tokens_openiddict_applications_application_id", x => x.application_id, "openiddict_applications", "id");
                table.ForeignKey("fk_openiddict_tokens_openiddict_authorizations_authorization_id", x => x.authorization_id, "openiddict_authorizations", "id");
            }
        );

        migrationBuilder.CreateIndex("ix_openiddict_applications_client_id", "openiddict_applications", "client_id", unique: true);
        migrationBuilder.CreateIndex("ix_openiddict_authorizations_application_id_status_subject_type", "openiddict_authorizations", ["application_id", "status", "subject", "type"]);
        migrationBuilder.CreateIndex("ix_openiddict_scopes_name", "openiddict_scopes", "name", unique: true);
        migrationBuilder.CreateIndex("ix_openiddict_tokens_application_id_status_subject_type", "openiddict_tokens", ["application_id", "status", "subject", "type"]);
        migrationBuilder.CreateIndex("ix_openiddict_tokens_authorization_id", "openiddict_tokens", "authorization_id");
        migrationBuilder.CreateIndex("ix_openiddict_tokens_reference_id", "openiddict_tokens", "reference_id", unique: true);
    }
}
