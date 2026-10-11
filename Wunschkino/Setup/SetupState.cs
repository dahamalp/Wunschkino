namespace Wunschkino.Setup;

/// <summary>Im Speicher gehaltener Einrichtungsstatus, damit die Redirect-Middleware nicht bei jedem Request die DB fragt.</summary>
public sealed class SetupState
{
    private volatile bool _isCompleted;

    public bool IsCompleted => _isCompleted;

    public void MarkCompleted() => _isCompleted = true;
}
