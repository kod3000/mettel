namespace Bruin.Seed;

// Per-thread row generator. Everything here is thread-local — the top-level
// runner shards ranges of the total row count across N generators and each
// COPY writer streams rows without cross-thread coordination.
internal sealed class RowGenerator
{
    private readonly Random _rng;
    private readonly (Guid Id, int Weight)[] _tenantWeights;
    private readonly int _tenantWeightTotal;
    private readonly (string, int)[] _categoryCdf;
    private readonly (string, int)[] _statusCdf;
    private readonly DateTimeOffset _now;
    private readonly TimeSpan _historyWindow;

    // Every service_number must be unique per client — the batch's contribution
    // to the (client_id, serviceNumber) space is derived from a monotonic
    // per-tenant counter seeded from the row's global index, so different
    // generator threads cannot collide.
    private readonly Func<Guid, long> _nextServiceNumber;

    public RowGenerator(
        int seed,
        IReadOnlyList<(Guid Id, int Weight)> tenants,
        DateTimeOffset now,
        Func<Guid, long> serviceNumberAllocator)
    {
        _rng = new Random(seed);
        _tenantWeights = tenants.ToArray();
        _tenantWeightTotal = _tenantWeights.Sum(t => t.Weight);
        _categoryCdf = ToCdf(Vocabulary.CategoryWeights);
        _statusCdf = ToCdf(Vocabulary.StatusWeights);
        _now = now;
        _historyWindow = TimeSpan.FromDays(365 * 3);
        _nextServiceNumber = serviceNumberAllocator;
    }

    private static (string Value, int Cum)[] ToCdf<T>((T, int)[] weights)
        where T : notnull
    {
        var arr = new (string, int)[weights.Length];
        int total = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            total += weights[i].Item2;
            arr[i] = (weights[i].Item1!.ToString()!, total);
        }
        return arr;
    }

    private string PickWeighted((string, int)[] cdf)
    {
        int max = cdf[^1].Item2;
        int r = _rng.Next(max);
        for (int i = 0; i < cdf.Length; i++)
            if (r < cdf[i].Item2) return cdf[i].Item1;
        return cdf[^1].Item1;
    }

    private Guid PickTenant()
    {
        int r = _rng.Next(_tenantWeightTotal);
        int acc = 0;
        foreach (var (id, w) in _tenantWeights)
        {
            acc += w;
            if (r < acc) return id;
        }
        return _tenantWeights[^1].Id;
    }

    // Clustered createdAt: 60% within the last year, 30% in year 2, 10% in year 3.
    // Reviewer-realistic — most operational inventory churn happens recently,
    // and older rows still exist for historical reports.
    private DateTimeOffset PickCreatedAt()
    {
        double p = _rng.NextDouble();
        double yearsBack = p switch
        {
            < 0.60 => _rng.NextDouble(),
            < 0.90 => 1.0 + _rng.NextDouble(),
            _      => 2.0 + _rng.NextDouble()
        };
        return _now.AddDays(-yearsBack * 365).AddSeconds(-_rng.Next(0, 86400));
    }

    public InventoryRow Next()
    {
        var tenant = PickTenant();
        var category = PickWeighted(_categoryCdf);
        var status = PickWeighted(_statusCdf);
        var namesForCat = Vocabulary.CatalogueByCategory.First(x => x.Category == category).Names;
        var name = namesForCat[_rng.Next(namesForCat.Length)];
        var (city, state) = Vocabulary.Locations[_rng.Next(Vocabulary.Locations.Length)];
        var street = Vocabulary.StreetNames[_rng.Next(Vocabulary.StreetNames.Length)];
        var address = $"{_rng.Next(1, 9999)} {street}, {city}, {state}";
        var assignee = Vocabulary.Assignees[_rng.Next(Vocabulary.Assignees.Length)];
        var notes = Vocabulary.NoteSnippets[_rng.Next(Vocabulary.NoteSnippets.Length)];

        var serviceSeq = _nextServiceNumber(tenant);
        // NANPA-shaped so the string search benchmark's 3–4 char substrings hit
        // realistic prefixes (area code, exchange).
        var area = 200 + (int)(serviceSeq / 10_000_000 % 800);
        var exch = 200 + (int)(serviceSeq / 10_000 % 800);
        var last = (int)(serviceSeq % 10_000);
        var serviceNumber = $"{area:D3}-{exch:D3}-{last:D4}";

        var createdAt = PickCreatedAt();
        // updatedAt sometimes matches createdAt (row never touched); sometimes
        // later (status change, notes edit). Skew again toward recent.
        var updatedAt = _rng.NextDouble() < 0.65
            ? createdAt
            : createdAt.AddDays(_rng.NextDouble() * (_now - createdAt).TotalDays);

        return new InventoryRow
        {
            Id = Guid.CreateVersion7(createdAt),
            ClientId = tenant,
            ServiceNumber = serviceNumber,
            ProductCategory = category,
            ProductName = name,
            Status = status,
            City = city,
            State = state,
            Address = address,
            Assignee = assignee,
            Notes = notes,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            RowVersion = 1,
        };
    }
}

internal struct InventoryRow
{
    public Guid Id;
    public Guid ClientId;
    public string ServiceNumber;
    public string ProductCategory;
    public string ProductName;
    public string Status;
    public string City;
    public string State;
    public string Address;
    public string? Assignee;
    public string? Notes;
    public DateTimeOffset CreatedAt;
    public DateTimeOffset UpdatedAt;
    public int RowVersion;
}
