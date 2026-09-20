using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>
/// Owns the replace/invoke/dispose boundary for one paper-body session. PaperWindow remains
/// responsible for WPF placement and provider selection; plugin exceptions stop here.
/// </summary>
internal sealed class PaperBodyHost
{
    public IPaperBodySession? Current { get; private set; }

    public bool HasCurrent => Current != null;

    public void Attach(IPaperBodySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Current != null && !ReferenceEquals(Current, session))
        {
            throw new InvalidOperationException(
                "A paper-body session is already attached.");
        }
        Current = session;
    }

    public Exception? Invoke(Action<IPaperBodySession> callback)
    {
        var session = Current;
        if (session == null)
        {
            return null;
        }

        try
        {
            callback(session);
            return null;
        }
        catch (Exception ex)
        {
            return ex.GetBaseException();
        }
    }

    public void CommitCancelDispose(bool cancelInteractions)
    {
        var session = Current;
        Current = null;
        if (session == null)
        {
            return;
        }

        try { session.Commit(); } catch { }
        if (cancelInteractions)
        {
            try { session.CancelInteractions(); } catch { }
        }
        try { session.Dispose(); } catch { }
    }
}
