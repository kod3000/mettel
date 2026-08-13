namespace Bruin.Web.Wasm.Services;

// Minimal imperative toast bus. Components inject ToastService and call
// Error/Info from anywhere — no cascading, no props threading. A single
// <Toaster /> mounted in MainLayout subscribes and renders the stack.
// Mirrors the React app's apps/web/src/components/Toaster.tsx.

public enum ToastKind { Error, Info }

public sealed record Toast(int Id, string Message, ToastKind Kind);

public sealed class ToastService
{
    private readonly List<Toast> _items = new();
    private int _seq;

    public event Action? OnChanged;

    public IReadOnlyList<Toast> Items => _items;

    public int Error(string message, int ttlMs = 8000) => Push(ToastKind.Error, message, ttlMs);
    public int Info(string message, int ttlMs = 4000)  => Push(ToastKind.Info,  message, ttlMs);

    public void Dismiss(int id)
    {
        if (_items.RemoveAll(t => t.Id == id) > 0)
            OnChanged?.Invoke();
    }

    private int Push(ToastKind kind, string message, int ttlMs)
    {
        var id = Interlocked.Increment(ref _seq);
        _items.Add(new Toast(id, message, kind));
        OnChanged?.Invoke();
        if (ttlMs > 0)
        {
            // Fire-and-forget auto-dismiss. Single-threaded WASM so no
            // synchronization primitive needed on the list.
            _ = Task.Delay(ttlMs).ContinueWith(_ => Dismiss(id), TaskScheduler.Default);
        }
        return id;
    }
}
