namespace PaperTodo;

internal sealed class UpdateHealthStore(UpdateStateStore stateStore)
{
    internal bool MarkLaunchStarted(string nonce, string targetPath, PaperTodoVersion version) =>
        stateStore.TryMarkLaunch(nonce, targetPath, version, healthy: false);

    internal bool MarkLaunchHealthy(string nonce, string targetPath, PaperTodoVersion version) =>
        stateStore.TryMarkLaunch(nonce, targetPath, version, healthy: true);
}
