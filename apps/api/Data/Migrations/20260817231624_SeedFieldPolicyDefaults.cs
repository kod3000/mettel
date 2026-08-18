using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bruin.Api.Data.Migrations
{
    /// <inheritdoc />
    // Seed `field_policy` so `/api/v1/me.adminOnlyFields` is non-empty in the
    // demo and the drawer actually locks fields for the `worker` role.
    //
    // Fields chosen:
    //   * notes    — free-text audit trail; giving every worker write access
    //                would let anyone rewrite the paper trail. Admin-only is
    //                the operationally-defensible default.
    //   * assignee — ownership assignment is a policy call (who's on-call for
    //                a row), so restricting it to admins matches the way most
    //                orgs handle scheduling.
    //
    // Idempotent via ON CONFLICT so re-running the migration on a volume that
    // already has these rows is a no-op.
    public partial class SeedFieldPolicyDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO public.field_policy (client_id, field_name, min_role)
                SELECT c.id, v.field_name, 'admin'
                FROM public.client c
                CROSS JOIN (VALUES ('notes'), ('assignee')) AS v(field_name)
                ON CONFLICT (client_id, field_name) DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DELETE FROM public.field_policy
                WHERE field_name IN ('notes', 'assignee') AND min_role = 'admin';");
        }
    }
}
