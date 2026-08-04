namespace Bruin.Seed;

// Fixed vocabularies so trigram + tsvector search have meaningful selectivity
// in benchmarks — random noise gives every query the same estimated cost, and
// then the plan-quality question isn't a real question.
internal static class Vocabulary
{
    public static readonly (string Category, string[] Names)[] CatalogueByCategory = new (string, string[])[]
    {
        ("voice", new[]
        {
            "Hosted PBX Seat", "SIP Trunk Standard", "SIP Trunk Premium",
            "Call Center Agent", "Voicemail Storage", "Toll Free 800 Number",
            "Analog POTS Line", "PRI T1 Line", "Softphone Client License",
            "Conference Bridge Port"
        }),
        ("data", new[]
        {
            "Fiber Internet 100M", "Fiber Internet 1G", "Fiber Internet 10G",
            "DSL Broadband Basic", "Cable Business 500M", "MPLS Circuit 50M",
            "Ethernet Point to Point", "Dedicated Internet Access",
            "Managed Router Gateway", "Static IP Block"
        }),
        ("wireless", new[]
        {
            "LTE Backup Modem", "5G Fixed Wireless", "Failover Wireless Gateway",
            "Mobile Broadband 25GB", "IoT SIM 500MB", "IoT SIM 10GB",
            "Cellular Router 5G", "M2M Data Plan Pooled",
            "Wireless Access Point", "Mesh Wireless Kit"
        }),
        ("other", new[]
        {
            "Web Hosting Bundle", "Domain Registration", "Email Hosting Seat",
            "SSL Certificate DV", "SSL Certificate EV", "DNS Management Service",
            "Cloud Backup 1TB", "Managed Firewall", "SD-WAN Edge Device",
            "Professional Services Hour"
        }),
    };

    // Weighted so voice + data dominate — matches Bruin's product mix and keeps
    // the status/category filter combinations reviewer-realistic.
    public static readonly (string Category, int Weight)[] CategoryWeights = new[]
    {
        ("voice", 40),
        ("data", 40),
        ("wireless", 15),
        ("other", 5),
    };

    // 15% pending, 70% active, 15% disconnected — a plausible operator mix.
    public static readonly (string Status, int Weight)[] StatusWeights = new[]
    {
        ("pending", 15),
        ("active", 70),
        ("disconnected", 15),
    };

    // 3 clients, uneven split so per-tenant benchmarks show real skew (~70/25/5).
    public static readonly (string Name, string ApiKey, int Weight)[] Tenants = new[]
    {
        ("Acme Telecom",       "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme",   70),
        ("Beacon Networks",    "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_beacon", 25),
        ("Cascade Communications", "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_cascade", 5),
    };

    // Sample US locations. `state` is a 2-char code — CHECK constraint doesn't
    // enforce this but the column length does.
    public static readonly (string City, string State)[] Locations = new[]
    {
        ("New York", "NY"), ("Los Angeles", "CA"), ("Chicago", "IL"),
        ("Houston", "TX"), ("Phoenix", "AZ"), ("Philadelphia", "PA"),
        ("San Antonio", "TX"), ("San Diego", "CA"), ("Dallas", "TX"),
        ("Austin", "TX"), ("Jacksonville", "FL"), ("Fort Worth", "TX"),
        ("Columbus", "OH"), ("Charlotte", "NC"), ("San Francisco", "CA"),
        ("Indianapolis", "IN"), ("Seattle", "WA"), ("Denver", "CO"),
        ("Boston", "MA"), ("Nashville", "TN"), ("Portland", "OR"),
        ("Las Vegas", "NV"), ("Detroit", "MI"), ("Memphis", "TN"),
        ("Louisville", "KY"), ("Baltimore", "MD"), ("Milwaukee", "WI"),
        ("Albuquerque", "NM"), ("Tucson", "AZ"), ("Fresno", "CA"),
        ("Sacramento", "CA"), ("Kansas City", "MO"), ("Atlanta", "GA"),
        ("Miami", "FL"), ("Raleigh", "NC"), ("Omaha", "NE"),
        ("Minneapolis", "MN"), ("Tulsa", "OK"), ("Cleveland", "OH"),
        ("Wichita", "KS"), ("Arlington", "VA"),
    };

    public static readonly string[] StreetNames = new[]
    {
        "Main St", "Oak Ave", "Maple Dr", "Cedar Ln", "Elm St", "Pine Rd",
        "Washington Blvd", "Lincoln Ave", "Jefferson St", "Park Ave",
        "Broadway", "Market St", "Church St", "Highland Dr", "Sunset Blvd",
        "Lakeview Dr", "Ridge Rd", "Hilltop Ave", "River Rd", "Valley Way"
    };

    public static readonly string[] Assignees = new[]
    {
        "j.doe", "a.smith", "m.chen", "r.patel", "k.nguyen", "s.johnson",
        "t.williams", "l.garcia", "d.brown", "b.martinez", "n.taylor",
        "p.anderson", "c.thompson", null!, null!, null!, // some rows have no assignee
    };

    public static readonly string?[] NoteSnippets = new string?[]
    {
        null, null, null, null, null, // most rows have no notes
        "customer requested rush install",
        "escalated by NOC on saturday",
        "billing hold pending PO",
        "wireless failover confirmed",
        "migration from legacy PRI complete",
        "port date locked",
        "site survey scheduled",
        "cross-connect through carrier hotel",
    };
}
