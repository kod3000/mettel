using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bruin.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExpandSearchTsv : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Extend the generated search_tsv column to cover more fields.
            //
            // Weights (higher weight = matches rank higher):
            //   A  product_name        (was A)
            //   B  address             (was B)
            //   C  notes               (was C)
            //   D  service_number, city, state, assignee   (NEW)
            //
            // Everything at weight D means a match against the number-shaped
            // service_number still shows up but ranks below a matching
            // product name — matches operator intuition for "find X" queries.
            //
            // Generated columns can't be ALTER'd in-place — DROP + ADD is the
            // only path. The GIN index over it drops via CASCADE and we
            // recreate it after. On a 3M-row table the rebuild is ~seconds
            // (single-tenant SPA — full table lock is acceptable).
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS public.ix_inventory_client_tsv;
                ALTER TABLE public.inventory DROP COLUMN IF EXISTS search_tsv;
                ALTER TABLE public.inventory
                ADD COLUMN search_tsv tsvector GENERATED ALWAYS AS (
                    setweight(to_tsvector('simple', coalesce(product_name, '')),    'A') ||
                    setweight(to_tsvector('simple', coalesce(address, '')),         'B') ||
                    setweight(to_tsvector('simple', coalesce(notes, '')),           'C') ||
                    setweight(to_tsvector('simple', coalesce(service_number, '')),  'D') ||
                    setweight(to_tsvector('simple', coalesce(city, '')),            'D') ||
                    setweight(to_tsvector('simple', coalesce(state, '')),           'D') ||
                    setweight(to_tsvector('simple', coalesce(assignee, '')),        'D')
                ) STORED;
                CREATE INDEX ix_inventory_client_tsv
                    ON public.inventory USING gin (client_id, search_tsv);
            ");

            // Refresh stats so the planner knows about the new column +
            // rebuilt index. Without this, first-touch queries fall back
            // to the (client_id) btree with a filter and take ~1.5s
            // instead of the ~200ms path via the GIN index.
            migrationBuilder.Sql("ANALYZE public.inventory;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS public.ix_inventory_client_tsv;
                ALTER TABLE public.inventory DROP COLUMN IF EXISTS search_tsv;
                ALTER TABLE public.inventory
                ADD COLUMN search_tsv tsvector GENERATED ALWAYS AS (
                    setweight(to_tsvector('simple', coalesce(product_name, '')), 'A') ||
                    setweight(to_tsvector('simple', coalesce(address, '')),      'B') ||
                    setweight(to_tsvector('simple', coalesce(notes, '')),        'C')
                ) STORED;
                CREATE INDEX ix_inventory_client_tsv
                    ON public.inventory USING gin (client_id, search_tsv);
            ");
        }
    }
}
