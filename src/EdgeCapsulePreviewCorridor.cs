namespace PaperTodo;

internal readonly record struct EdgeCapsulePreviewCorridorNode(
    DeviceScreenRect Bounds,
    bool ConnectToPrevious);

/// <summary>
/// Builds the temporary empty transfer rectangle for each uninterrupted run of real interactive
/// capsules. These rectangles are pointer-intent geometry only; they never become hit-test bounds.
/// </summary>
internal static class EdgeCapsulePreviewCorridor
{
    public static bool Contains(
        ReadOnlySpan<EdgeCapsulePreviewCorridorNode> nodes,
        DeviceScreenPoint pointer)
    {
        if (nodes.IsEmpty)
        {
            return false;
        }

        var componentLeft = 0;
        var componentTop = 0;
        var componentRight = 0;
        var componentBottom = 0;
        var componentHasBounds = false;
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            if (node.Bounds.IsEmpty)
            {
                if (componentHasBounds && Contains(
                        new DeviceScreenRect(
                            componentLeft,
                            componentTop,
                            componentRight,
                            componentBottom),
                        pointer))
                {
                    return true;
                }
                componentHasBounds = false;
                continue;
            }

            if (componentHasBounds && !node.ConnectToPrevious)
            {
                if (Contains(
                        new DeviceScreenRect(
                            componentLeft,
                            componentTop,
                            componentRight,
                            componentBottom),
                        pointer))
                {
                    return true;
                }
                componentHasBounds = false;
            }

            if (!componentHasBounds)
            {
                componentLeft = node.Bounds.Left;
                componentTop = node.Bounds.Top;
                componentRight = node.Bounds.Right;
                componentBottom = node.Bounds.Bottom;
                componentHasBounds = true;
                continue;
            }

            componentLeft = Math.Min(componentLeft, node.Bounds.Left);
            componentTop = Math.Min(componentTop, node.Bounds.Top);
            componentRight = Math.Max(componentRight, node.Bounds.Right);
            componentBottom = Math.Max(componentBottom, node.Bounds.Bottom);
        }

        return componentHasBounds && Contains(
            new DeviceScreenRect(
                componentLeft,
                componentTop,
                componentRight,
                componentBottom),
            pointer);
    }

    private static bool Contains(
        DeviceScreenRect bounds,
        DeviceScreenPoint pointer) =>
        !bounds.IsEmpty &&
        pointer.X >= bounds.Left &&
        pointer.X < bounds.Right &&
        pointer.Y >= bounds.Top &&
        pointer.Y < bounds.Bottom;

}
