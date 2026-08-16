using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bruin.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInventorySoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                schema: "public",
                table: "inventory",
                type: "timestamptz",
                nullable: true);

            // Recreate the unique service_number constraint as a partial
            // index that excludes tombstoned rows — otherwise deleting a
            // row would permanently reserve the service_number and prevent
            // ever re-creating a row with the same number.
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS public.ux_inventory_client_service;
                CREATE UNIQUE INDEX ux_inventory_client_service
                    ON public.inventory (client_id, service_number)
                    WHERE deleted_at IS NULL;");

            // Cheap partial index so `WHERE deleted_at IS NOT NULL` scans
            // (admin tombstone list, future purge job) don't full-scan.
            migrationBuilder.Sql(@"
                CREATE INDEX ix_inventory_deleted_at
                    ON public.inventory (client_id, deleted_at)
                    WHERE deleted_at IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS public.ix_inventory_deleted_at;
                DROP INDEX IF EXISTS public.ux_inventory_client_service;
                CREATE UNIQUE INDEX ux_inventory_client_service
                    ON public.inventory (client_id, service_number);");
            migrationBuilder.DropColumn(
                name: "deleted_at",
                schema: "public",
                table: "inventory");
        }
    }
}
