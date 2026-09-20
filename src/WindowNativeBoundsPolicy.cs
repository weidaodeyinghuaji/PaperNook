namespace PaperTodo;

/// <summary>
/// Adds only the native no-change flags that are proven by the current HWND rectangle. Keeping
/// this policy pure keeps the strict axis contract independently testable without invoking User32
/// from the regression checks.
/// </summary>
internal static class WindowNativeBoundsPolicy
{
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;

    internal static uint FlagsForChanges(
        uint baseFlags,
        bool positionChanged,
        bool sizeChanged)
    {
        if (!positionChanged)
        {
            baseFlags |= SwpNoMove;
        }
        if (!sizeChanged)
        {
            baseFlags |= SwpNoSize;
        }

        return baseFlags;
    }
}
