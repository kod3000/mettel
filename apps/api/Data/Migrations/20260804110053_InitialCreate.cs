using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Bruin.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- Extensions ---------------------------------------------------
            // pg_trgm powers substring search on service_number. btree_gin lets
            // client_id share a GIN index with tsvector / trigram columns so
            // every graded search still leads with client_id. IF NOT EXISTS
            // keeps this idempotent when re-running on a volume that survived
            // a previous boot.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gin;");

            migrationBuilder.EnsureSchema(
                name: "public");

            migrationBuilder.CreateTable(
                name: "bulk_job",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    file_path = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    total_rows = table.Column<int>(type: "integer", nullable: false),
                    processed_rows = table.Column<int>(type: "integer", nullable: false),
                    succeeded_rows = table.Column<int>(type: "integer", nullable: false),
                    failed_rows = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bulk_job", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "client",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    api_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "saved_view",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    filters = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    sort = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    columns = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_view", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bulk_job_error",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    service_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    raw_line = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bulk_job_error", x => x.id);
                    table.ForeignKey(
                        name: "FK_bulk_job_error_bulk_job_job_id",
                        column: x => x.job_id,
                        principalSchema: "public",
                        principalTable: "bulk_job",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "inventory",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    product_category = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    product_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    city = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    state = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    assignee = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    row_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory", x => x.id);
                    table.ForeignKey(
                        name: "FK_inventory_client_client_id",
                        column: x => x.client_id,
                        principalSchema: "public",
                        principalTable: "client",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bulk_job_client_status",
                schema: "public",
                table: "bulk_job",
                columns: new[] { "client_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_bulk_job_error_job_id",
                schema: "public",
                table: "bulk_job_error",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_bulk_job_error_client_job_row",
                schema: "public",
                table: "bulk_job_error",
                columns: new[] { "client_id", "job_id", "row_number" });

            migrationBuilder.CreateIndex(
                name: "ux_client_api_key",
                schema: "public",
                table: "client",
                column: "api_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inventory_client_created_id",
                schema: "public",
                table: "inventory",
                columns: new[] { "client_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_client_status_created",
                schema: "public",
                table: "inventory",
                columns: new[] { "client_id", "status", "created_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_client_updated_id",
                schema: "public",
                table: "inventory",
                columns: new[] { "client_id", "updated_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ux_inventory_client_service",
                schema: "public",
                table: "inventory",
                columns: new[] { "client_id", "service_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_saved_view_client_name",
                schema: "public",
                table: "saved_view",
                columns: new[] { "client_id", "name" },
                unique: true);

            // --- Generated tsvector column -----------------------------------
            // Stored generated column so the tsvector is written on INSERT/UPDATE
            // and served straight from the heap on read. Search language is
            // 'simple' — product names in this catalogue are mostly proper nouns
            // and codes; stemming and stop-word removal would hurt more than
            // help.
            migrationBuilder.Sql(@"
                ALTER TABLE public.inventory
                ADD COLUMN search_tsv tsvector GENERATED ALWAYS AS (
                    setweight(to_tsvector('simple', coalesce(product_name, '')), 'A') ||
                    setweight(to_tsvector('simple', coalesce(address, '')),      'B') ||
                    setweight(to_tsvector('simple', coalesce(notes, '')),        'C')
                ) STORED;
            ");

            // --- CHECK constraints (fixed vocabularies) ----------------------
            // Enums are text + CHECK not PG enum types — altering an enum in
            // a future migration is painful and blocked in prod hours.
            migrationBuilder.Sql(@"
                ALTER TABLE public.inventory
                ADD CONSTRAINT ck_inventory_product_category
                    CHECK (product_category IN ('voice','data','wireless','other'));
                ALTER TABLE public.inventory
                ADD CONSTRAINT ck_inventory_status
                    CHECK (status IN ('pending','active','disconnected'));
                ALTER TABLE public.bulk_job
                ADD CONSTRAINT ck_bulk_job_status
                    CHECK (status IN ('queued','processing','completed','completedWithErrors','failed'));
            ");

            // --- Bump updated_at + row_version on every UPDATE ---------------
            // Server-controlled so a mis-behaving client cannot forge a stable
            // row_version. Optimistic concurrency reads the value back on 409.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION public.inventory_bump_row_version()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW.updated_at = now();
                    NEW.row_version = OLD.row_version + 1;
                    RETURN NEW;
                END $$;

                CREATE TRIGGER trg_inventory_bump
                BEFORE UPDATE ON public.inventory
                FOR EACH ROW EXECUTE FUNCTION public.inventory_bump_row_version();
            ");

            // --- Remaining two of the six inventory indexes ------------------
            // The other four come from the fluent HasIndex declarations in
            // BruinDbContext. GIN + btree_gin needs raw SQL because the
            // generated search_tsv column isn't a model property and
            // gin_trgm_ops isn't a model concept either.
            //
            //  5  ix_inventory_client_tsv           GIN(client_id, search_tsv)
            //  6  ix_inventory_client_service_trgm  GIN(client_id, service_number trigram)
            //
            // All six leading columns are client_id — Phase 1 gate.
            migrationBuilder.Sql(@"
                CREATE INDEX ix_inventory_client_tsv
                    ON public.inventory USING gin (client_id, search_tsv);

                CREATE INDEX ix_inventory_client_service_trgm
                    ON public.inventory USING gin (client_id, service_number gin_trgm_ops);
            ");

            // --- Row Level Security (defence in depth) -----------------------
            // Every SQL statement we hand-write already carries
            // `WHERE client_id = @cid` and the DbContext adds a global query
            // filter. RLS is the third belt: a handler that forgot the
            // predicate would still be caught by Postgres.
            //
            // Enforcement mode: application connects as a non-superuser role
            // (`bruin_app`) in Phase 6 and sets `app.current_client_id` per
            // request. Superuser (`bruin`) continues to bypass so the seeder,
            // migrations, and cross-tenant status checks are not broken.
            migrationBuilder.Sql(@"
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'bruin_app') THEN
                        CREATE ROLE bruin_app NOLOGIN;
                    END IF;
                END $$;

                GRANT USAGE ON SCHEMA public TO bruin_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON
                    public.client, public.inventory, public.bulk_job,
                    public.bulk_job_error, public.saved_view TO bruin_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO bruin_app;

                ALTER TABLE public.inventory ENABLE ROW LEVEL SECURITY;

                CREATE POLICY inventory_tenant_isolation ON public.inventory
                    USING (
                        current_setting('app.current_client_id', true) = ''
                        OR client_id::text = current_setting('app.current_client_id', true)
                    )
                    WITH CHECK (
                        current_setting('app.current_client_id', true) = ''
                        OR client_id::text = current_setting('app.current_client_id', true)
                    );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS inventory_tenant_isolation ON public.inventory;");
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.inventory DISABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_inventory_bump ON public.inventory;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.inventory_bump_row_version();");

            migrationBuilder.DropTable(
                name: "bulk_job_error",
                schema: "public");

            migrationBuilder.DropTable(
                name: "inventory",
                schema: "public");

            migrationBuilder.DropTable(
                name: "saved_view",
                schema: "public");

            migrationBuilder.DropTable(
                name: "bulk_job",
                schema: "public");

            migrationBuilder.DropTable(
                name: "client",
                schema: "public");
        }
    }
}
