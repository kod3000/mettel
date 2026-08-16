using Bruin.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bruin.Api.Data;

// EF Core owns writes. The graded list-read path bypasses this entirely and
// goes through Dapper (Phase 3). Keeping EF here for CRUD, migrations, and
// the tenancy global query filter (defence in depth).
public sealed class BruinDbContext(
    DbContextOptions<BruinDbContext> options,
    ITenantContext? tenant = null) : DbContext(options)
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<FieldPolicy> FieldPolicies => Set<FieldPolicy>();
    public DbSet<Inventory> Inventory => Set<Inventory>();
    public DbSet<BulkJob> BulkJobs => Set<BulkJob>();
    public DbSet<BulkJobError> BulkJobErrors => Set<BulkJobError>();
    public DbSet<SavedView> SavedViews => Set<SavedView>();

    // Exposed so migration-time / seed-time callers can create the context
    // without a request-scoped tenant.
    private readonly ITenantContext? _tenant = tenant;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("public");

        b.Entity<Client>(e =>
        {
            e.ToTable("client");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.ApiKey).HasColumnName("api_key").HasMaxLength(128).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz")
                .HasDefaultValueSql("now()");
            e.HasIndex(x => x.ApiKey).IsUnique().HasDatabaseName("ux_client_api_key");
        });

        b.Entity<ApiKey>(e =>
        {
            e.ToTable("api_key");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid");
            e.Property(x => x.ClientId).HasColumnName("client_id").HasColumnType("uuid").IsRequired();
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(128).IsRequired();
            e.Property(x => x.Role).HasColumnName("role").HasMaxLength(16).IsRequired();
            e.Property(x => x.Label).HasColumnName("label").HasMaxLength(120);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz")
                .HasDefaultValueSql("now()");
            e.HasIndex(x => x.Key).IsUnique().HasDatabaseName("ux_api_key_key");
            e.HasIndex(x => new { x.ClientId, x.Role }).HasDatabaseName("ix_api_key_client_role");
            e.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FieldPolicy>(e =>
        {
            e.ToTable("field_policy");
            e.HasKey(x => new { x.ClientId, x.FieldName });
            e.Property(x => x.ClientId).HasColumnName("client_id").HasColumnType("uuid");
            e.Property(x => x.FieldName).HasColumnName("field_name").HasMaxLength(64);
            e.Property(x => x.MinRole).HasColumnName("min_role").HasMaxLength(16).IsRequired();
            e.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Inventory>(e =>
        {
            e.ToTable("inventory");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid");
            e.Property(x => x.ClientId).HasColumnName("client_id").HasColumnType("uuid").IsRequired();
            e.Property(x => x.ServiceNumber).HasColumnName("service_number").HasMaxLength(64).IsRequired();
            e.Property(x => x.ProductCategory).HasColumnName("product_category").HasMaxLength(16).IsRequired();
            e.Property(x => x.ProductName).HasColumnName("product_name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            e.Property(x => x.City).HasColumnName("city").HasMaxLength(120);
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(2);
            e.Property(x => x.Address).HasColumnName("address").HasMaxLength(300);
            e.Property(x => x.Assignee).HasColumnName("assignee").HasMaxLength(120);
            e.Property(x => x.Notes).HasColumnName("notes").HasColumnType("text");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz")
                .HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz")
                .HasDefaultValueSql("now()");
            e.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1);
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");

            // FK is scoped explicitly so RLS on inventory doesn't accidentally
            // cascade to client rows.
            e.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);

            // Four of the six inventory indexes — all leading with client_id.
            // The remaining two (GIN over (client_id, search_tsv) and the
            // (client_id, service_number) trigram) are raw SQL in the migration
            // because the generated search_tsv column and gin_trgm_ops aren't
            // model-visible. See migration comments for the full six.
            e.HasIndex(x => new { x.ClientId, x.ServiceNumber })
                .IsUnique()
                .HasDatabaseName("ux_inventory_client_service");
            e.HasIndex(x => new { x.ClientId, x.CreatedAt, x.Id })
                .IsDescending(false, true, true)
                .HasDatabaseName("ix_inventory_client_created_id");
            e.HasIndex(x => new { x.ClientId, x.UpdatedAt, x.Id })
                .IsDescending(false, true, true)
                .HasDatabaseName("ix_inventory_client_updated_id");
            e.HasIndex(x => new { x.ClientId, x.Status, x.CreatedAt, x.Id })
                .IsDescending(false, false, true, true)
                .HasDatabaseName("ix_inventory_client_status_created");

            // Global query filter — belt for the braces that is every explicit
            // `WHERE client_id = @cid` we hand-write. Superseded by RLS at the DB
            // level when the API connects as the non-superuser role.
            e.HasQueryFilter(x => _tenant == null || _tenant.ClientId == null || x.ClientId == _tenant.ClientId);
        });

        b.Entity<BulkJob>(e =>
        {
            e.ToTable("bulk_job");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid");
            e.Property(x => x.ClientId).HasColumnName("client_id").HasColumnType("uuid").IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(24).IsRequired();
            e.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(300).IsRequired();
            e.Property(x => x.FilePath).HasColumnName("file_path").HasMaxLength(600).IsRequired();
            e.Property(x => x.TotalRows).HasColumnName("total_rows");
            e.Property(x => x.ProcessedRows).HasColumnName("processed_rows");
            e.Property(x => x.SucceededRows).HasColumnName("succeeded_rows");
            e.Property(x => x.FailedRows).HasColumnName("failed_rows");
            e.Property(x => x.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamptz");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz")
                .HasDefaultValueSql("now()");

            // Compound index leading with client_id — worker's `SELECT … FOR
            // UPDATE SKIP LOCKED` (Phase 10) still targets the tenant it runs for
            // even when we scale workers.
            e.HasIndex(x => new { x.ClientId, x.Status, x.CreatedAt }).HasDatabaseName("ix_bulk_job_client_status");

            e.HasQueryFilter(x => _tenant == null || _tenant.ClientId == null || x.ClientId == _tenant.ClientId);
        });

        b.Entity<BulkJobError>(e =>
        {
            e.ToTable("bulk_job_error");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.JobId).HasColumnName("job_id").HasColumnType("uuid").IsRequired();
            e.Property(x => x.ClientId).HasColumnName("client_id").HasColumnType("uuid").IsRequired();
            e.Property(x => x.RowNumber).HasColumnName("row_number");
            e.Property(x => x.ServiceNumber).HasColumnName("service_number").HasMaxLength(64);
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();
            e.Property(x => x.RawLine).HasColumnName("raw_line").HasColumnType("text").IsRequired();

            e.HasOne<BulkJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ClientId, x.JobId, x.RowNumber }).HasDatabaseName("ix_bulk_job_error_client_job_row");

            e.HasQueryFilter(x => _tenant == null || _tenant.ClientId == null || x.ClientId == _tenant.ClientId);
        });

        b.Entity<SavedView>(e =>
        {
            e.ToTable("saved_view");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid");
            e.Property(x => x.ClientId).HasColumnName("client_id").HasColumnType("uuid").IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Filters).HasColumnName("filters").HasColumnType("jsonb");
            e.Property(x => x.Sort).HasColumnName("sort").HasColumnType("jsonb");
            e.Property(x => x.Columns).HasColumnName("columns").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz")
                .HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz")
                .HasDefaultValueSql("now()");

            e.HasIndex(x => new { x.ClientId, x.Name }).IsUnique().HasDatabaseName("ux_saved_view_client_name");

            e.HasQueryFilter(x => _tenant == null || _tenant.ClientId == null || x.ClientId == _tenant.ClientId);
        });

        base.OnModelCreating(b);
    }
}
