namespace Bruin.Web.Wasm.Layout;

// Tiny event bus so the top-bar buttons (in MainLayout) can trigger
// modals rendered inside Home (which sits in the layout's @Body slot).
// Not a service registration because it's local to the layout/page
// pair — Home cascades in via [CascadingParameter].
public sealed class AppActions
{
    public event Action? OpenCreateRequested;
    public event Action? OpenApiRefRequested;

    public void RaiseOpenCreate() => OpenCreateRequested?.Invoke();
    public void RaiseOpenApiRef() => OpenApiRefRequested?.Invoke();
}
