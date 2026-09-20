namespace PaperTodo;

public sealed partial class AppController
{
    private void SuspendPluginShortcutRegistrations()
    {
        // Keep configured reservations inside the process-global broker while releasing only this
        // owner's active RegisterHotKey entries. That prevents a built-in shortcut transaction from
        // stealing a plugin key merely because the plugin is temporarily suspended for recording or
        // numpad-mode reconciliation.
        _pluginHotkeys?.Suspend();
    }
}
