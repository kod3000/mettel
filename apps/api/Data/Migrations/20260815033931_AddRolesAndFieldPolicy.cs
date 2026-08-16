using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bruin.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRolesAndFieldPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_key",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_key", x => x.id);
                    table.ForeignKey(
                        name: "FK_api_key_client_client_id",
                        column: x => x.client_id,
                        principalSchema: "public",
                        principalTable: "client",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "field_policy",
                schema: "public",
                columns: table => new
                {
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    min_role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_policy", x => new { x.client_id, x.field_name });
                    table.ForeignKey(
                        name: "FK_field_policy_client_client_id",
                        column: x => x.client_id,
                        principalSchema: "public",
                        principalTable: "client",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_key_client_role",
                schema: "public",
                table: "api_key",
                columns: new[] { "client_id", "role" });

            migrationBuilder.CreateIndex(
                name: "ux_api_key_key",
                schema: "public",
                table: "api_key",
                column: "key",
                unique: true);

            // --- CHECK constraints ------------------------------------------
            // Belt for the code-level Roles.IsKnown check. Prevents a stray
            // seed/manual INSERT from planting an unresolvable role that would
            // 500 the auth middleware.
            migrationBuilder.Sql(@"
                ALTER TABLE public.api_key
                    ADD CONSTRAINT ck_api_key_role
                    CHECK (role IN ('admin','worker','reader'));");
            migrationBuilder.Sql(@"
                ALTER TABLE public.field_policy
                    ADD CONSTRAINT ck_field_policy_min_role
                    CHECK (min_role IN ('admin','worker'));");

            // --- Backfill existing client keys as admin ---------------------
            // Every row currently in `client` gets a matching row in `api_key`
            // with role='admin' so pre-migration keys continue to work
            // unchanged. `client.api_key` is left in place for now; the
            // resolver switch (Track A2) reads from api_key first, then
            // falls back to client.api_key while the transition settles.
            migrationBuilder.Sql(@"
                INSERT INTO public.api_key (id, client_id, key, role, label, created_at)
                SELECT gen_random_uuid(), c.id, c.api_key, 'admin',
                       'legacy admin key (backfill from client.api_key)', now()
                FROM public.client c
                ON CONFLICT (key) DO NOTHING;");

            // --- Seed worker + reader keys per client -----------------------
            // Predictable suffixes so the demo picker (or a curl reproducer)
            // can construct them from the tenant name without a round-trip.
            // Idempotent via ON CONFLICT so re-applying the migration to a
            // volume that already has seed keys is safe.
            migrationBuilder.Sql(@"
                INSERT INTO public.api_key (id, client_id, key, role, label, created_at)
                SELECT gen_random_uuid(), c.id, c.api_key || '_worker', 'worker',
                       'seeded worker key', now()
                FROM public.client c
                ON CONFLICT (key) DO NOTHING;
                INSERT INTO public.api_key (id, client_id, key, role, label, created_at)
                SELECT gen_random_uuid(), c.id, c.api_key || '_reader', 'reader',
                       'seeded reader key', now()
                FROM public.client c
                ON CONFLICT (key) DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_key",
                schema: "public");

            migrationBuilder.DropTable(
                name: "field_policy",
                schema: "public");
        }
    }
}
