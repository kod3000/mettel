namespace Bruin.Api.Domain;

// Single source of truth for status-transition rules — used by both the
// single-update PATCH path and the CSV bulk worker so operator + import see
// the same law. Adding a new status means editing this table and nothing
// else.
//
// Contract-mandated rules (docs/API_CONTRACT.md):
//   pending -> active            allowed
//   pending -> disconnected      allowed (operator "cancel before activation")
//   active  -> disconnected      allowed
//   everything else              rejected with `invalid-status-transition`
//     including same-state (`active -> active`) and reverses
//     (`active -> pending`, `disconnected -> anything`).
//
// The Create path additionally forbids landing directly in `disconnected` —
// that lives on the write handler, not here, because it's an initial-value
// constraint rather than a transition.
public static class StatusTransitions
{
    private static readonly HashSet<(string From, string To)> _allowed = new()
    {
        (InventoryStatuses.Pending, InventoryStatuses.Active),
        (InventoryStatuses.Pending, InventoryStatuses.Disconnected),
        (InventoryStatuses.Active,  InventoryStatuses.Disconnected),
    };

    public static bool IsAllowed(string from, string to) => _allowed.Contains((from, to));

    // Convenience — enumerable so tests can iterate the full matrix.
    public static IEnumerable<(string From, string To, bool Allowed)> Matrix()
    {
        foreach (var f in InventoryStatuses.All)
            foreach (var t in InventoryStatuses.All)
                yield return (f, t, IsAllowed(f, t));
    }
}
