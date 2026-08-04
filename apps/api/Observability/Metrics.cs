using System.Threading;

namespace Bruin.Api.Observability;

// Minimal in-process counters/gauges. Full Prometheus text format at /metrics.
// Kept dependency-free — pulling prometheus-net for one counter is overkill.
public sealed class Metrics
{
    // Every time the read router picks primary instead of replica (fallback
    // due to lag). Rate should be low in steady state; a spike means the
    // replica is falling behind.
    private long _readPrimaryFallbackTotal;
    public long ReadPrimaryFallbackTotal => Interlocked.Read(ref _readPrimaryFallbackTotal);
    public void IncReadPrimaryFallback() => Interlocked.Increment(ref _readPrimaryFallbackTotal);

    // Every replica-lag observation. Set by the ReplicaState cache refresh.
    private double _replicaLagSeconds;
    public double ReplicaLagSeconds => Interlocked.Exchange(ref _replicaLagSeconds, _replicaLagSeconds);
    public void SetReplicaLagSeconds(double v) => Interlocked.Exchange(ref _replicaLagSeconds, v);

    // Prometheus text format. Only the two custom series today — Kestrel /
    // ASPNETCORE emit their own via System.Diagnostics.Metrics if the
    // reviewer wires an exporter.
    public string RenderPrometheus()
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append("# HELP bruin_read_primary_fallback_total Times the read router chose primary because the replica hadn't caught up.\n");
        sb.Append("# TYPE bruin_read_primary_fallback_total counter\n");
        sb.Append("bruin_read_primary_fallback_total ").Append(ReadPrimaryFallbackTotal).Append('\n');
        sb.Append("# HELP bruin_replica_lag_seconds Seconds between the primary's last-written commit and the replica's last-applied commit.\n");
        sb.Append("# TYPE bruin_replica_lag_seconds gauge\n");
        sb.Append("bruin_replica_lag_seconds ").Append(ReplicaLagSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }
}
