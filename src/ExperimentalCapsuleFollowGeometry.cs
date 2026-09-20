namespace PaperTodo;

internal enum ExperimentalCapsuleFollowMode
{
    Revealed,
    RetractedBehindTarget,
    RetractedAtScreenEdge
}

internal readonly record struct ExperimentalCapsuleFollowPlan(
    DeviceScreenRect Bounds,
    ExperimentalCapsuleFollowMode Mode,
    bool HeadUsesCloseSegment)
{
    public bool IsRetracted =>
        Mode != ExperimentalCapsuleFollowMode.Revealed;

    public bool IsBehindTarget =>
        Mode == ExperimentalCapsuleFollowMode.RetractedBehindTarget;
}

internal static partial class ExperimentalWindowAttachmentGeometry
{
    private const double CapsulePeekWidthDip = 26;
    private const double CapsulePeekHeightDip = 12;

    public static ExperimentalCapsuleFollowPlan ResolveCapsuleFollow(
        ExperimentalWindowAttachmentSession session,
        DeviceScreenRect targetBounds,
        DeviceScreenRect currentWindowBounds,
        MonitorGeometry monitor,
        IReadOnlyList<MonitorGeometry> connectedMonitors,
        bool reveal)
    {
        var edgeScale = session.Edge is
            ExperimentalAttachmentEdge.Left or
            ExperimentalAttachmentEdge.Right
                ? monitor.DpiScaleX
                : monitor.DpiScaleY;
        var outside = Resolve(
            session with
            {
                InsideTarget = false,
                GapDip = 0
            },
            targetBounds,
            currentWindowBounds,
            edgeScale);
        if (outside.IsEmpty ||
            session.TargetKind !=
                ExperimentalAttachmentTargetKind.ExternalWindow)
        {
            return new ExperimentalCapsuleFollowPlan(
                outside,
                ExperimentalCapsuleFollowMode.Revealed,
                HeadUsesCloseSegment: false);
        }

        var targetCoversMonitor = TargetCoversWorkArea(
            targetBounds,
            monitor.WorkArea,
            Math.Max(
                monitor.DpiScaleX,
                monitor.DpiScaleY));
        var hasReachableOutsideHead =
            HasCapsulePeekOnAnyMonitor(
                outside,
                connectedMonitors,
                session.Edge);
        if (!reveal)
        {
            return !targetCoversMonitor &&
                hasReachableOutsideHead
                    ? new ExperimentalCapsuleFollowPlan(
                        PlaceCapsuleBehindTarget(
                            outside,
                            targetBounds,
                            session.Edge,
                            monitor.DpiScaleX,
                            monitor.DpiScaleY),
                        ExperimentalCapsuleFollowMode
                            .RetractedBehindTarget,
                        HeadUsesCloseSegment:
                            session.Edge !=
                            ExperimentalAttachmentEdge.Left)
                    : new ExperimentalCapsuleFollowPlan(
                        PlaceCapsuleAtScreenEdge(
                            outside,
                            monitor.WorkArea,
                            session.Edge,
                            monitor.DpiScaleX,
                            monitor.DpiScaleY),
                        ExperimentalCapsuleFollowMode
                            .RetractedAtScreenEdge,
                        HeadUsesCloseSegment:
                            session.Edge !=
                            ExperimentalAttachmentEdge.Right);
        }

        if (!targetCoversMonitor &&
            connectedMonitors.Any(candidate =>
                Contains(candidate.WorkArea, outside)))
        {
            return new ExperimentalCapsuleFollowPlan(
                outside,
                ExperimentalCapsuleFollowMode.Revealed,
                HeadUsesCloseSegment: false);
        }

        if (!targetCoversMonitor && hasReachableOutsideHead)
        {
            // Near a monitor boundary there may be room for the exposed head
            // but not for the full outside capsule. Keep the same anchor and
            // reveal by raising it above the target; crossing to the opposite
            // side would move the capsule out from under the pointer and cause
            // an enter/leave loop.
            return new ExperimentalCapsuleFollowPlan(
                PlaceCapsuleBehindTarget(
                    outside,
                    targetBounds,
                    session.Edge,
                    monitor.DpiScaleX,
                    monitor.DpiScaleY),
                ExperimentalCapsuleFollowMode.Revealed,
                HeadUsesCloseSegment: false);
        }

        var inside = Resolve(
            session with
            {
                InsideTarget = true,
                GapDip = 0
            },
            targetBounds,
            currentWindowBounds,
            edgeScale);
        return new ExperimentalCapsuleFollowPlan(
            KeepContained(inside, monitor.WorkArea),
            ExperimentalCapsuleFollowMode.Revealed,
            HeadUsesCloseSegment: false);
    }

    public static DeviceScreenRect KeepContained(
        DeviceScreenRect bounds,
        DeviceScreenRect workArea)
    {
        if (bounds.IsEmpty || workArea.IsEmpty)
        {
            return bounds;
        }

        var left = Math.Clamp(
            bounds.Left,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - bounds.Width));
        var top = Math.Clamp(
            bounds.Top,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - bounds.Height));
        return new DeviceScreenRect(
            left,
            top,
            left + bounds.Width,
            top + bounds.Height);
    }

    private static bool TargetCoversWorkArea(
        DeviceScreenRect target,
        DeviceScreenRect workArea,
        double dpiScale)
    {
        var tolerance = Math.Max(
            2,
            RoundDevice(2 * Math.Max(1, dpiScale)));
        return target.Left <= workArea.Left + tolerance &&
            target.Top <= workArea.Top + tolerance &&
            target.Right >= workArea.Right - tolerance &&
            target.Bottom >= workArea.Bottom - tolerance;
    }

    private static bool HasCapsulePeekOnAnyMonitor(
        DeviceScreenRect bounds,
        IReadOnlyList<MonitorGeometry> monitors,
        ExperimentalAttachmentEdge edge)
    {
        foreach (var monitor in monitors)
        {
            var area = monitor.WorkArea;
            var visibleWidth = Math.Max(
                0,
                Math.Min(bounds.Right, area.Right) -
                Math.Max(bounds.Left, area.Left));
            var visibleHeight = Math.Max(
                0,
                Math.Min(bounds.Bottom, area.Bottom) -
                Math.Max(bounds.Top, area.Top));
            var (peekWidth, peekHeight) = CapsulePeekSize(
                bounds,
                monitor.DpiScaleX,
                monitor.DpiScaleY);
            if (edge is
                ExperimentalAttachmentEdge.Left or
                ExperimentalAttachmentEdge.Right)
            {
                if (visibleWidth >= peekWidth &&
                    visibleHeight >= Math.Min(
                        bounds.Height,
                        peekHeight))
                {
                    return true;
                }
            }
            else if (visibleHeight >= peekHeight &&
                visibleWidth >= Math.Min(bounds.Width, peekWidth))
            {
                return true;
            }
        }

        return false;
    }

    private static DeviceScreenRect PlaceCapsuleBehindTarget(
        DeviceScreenRect desired,
        DeviceScreenRect target,
        ExperimentalAttachmentEdge edge,
        double dpiScaleX,
        double dpiScaleY)
    {
        var (peekX, peekY) = CapsulePeekSize(
            desired,
            dpiScaleX,
            dpiScaleY);
        var left = desired.Left;
        var top = desired.Top;
        switch (edge)
        {
            case ExperimentalAttachmentEdge.Left:
                left = target.Left - peekX;
                break;
            case ExperimentalAttachmentEdge.Right:
                left = target.Right - desired.Width + peekX;
                break;
            case ExperimentalAttachmentEdge.Top:
                top = target.Top - peekY;
                break;
            case ExperimentalAttachmentEdge.Bottom:
                top = target.Bottom - desired.Height + peekY;
                break;
        }

        return new DeviceScreenRect(
            left,
            top,
            left + desired.Width,
            top + desired.Height);
    }

    private static DeviceScreenRect PlaceCapsuleAtScreenEdge(
        DeviceScreenRect desired,
        DeviceScreenRect workArea,
        ExperimentalAttachmentEdge edge,
        double dpiScaleX,
        double dpiScaleY)
    {
        var (peekX, peekY) = CapsulePeekSize(
            desired,
            dpiScaleX,
            dpiScaleY);
        var left = desired.Left;
        var top = desired.Top;
        switch (edge)
        {
            case ExperimentalAttachmentEdge.Left:
                left = workArea.Left + peekX - desired.Width;
                break;
            case ExperimentalAttachmentEdge.Right:
                left = workArea.Right - peekX;
                break;
            case ExperimentalAttachmentEdge.Top:
                top = workArea.Top + peekY - desired.Height;
                break;
            case ExperimentalAttachmentEdge.Bottom:
                top = workArea.Bottom - peekY;
                break;
        }

        return new DeviceScreenRect(
            left,
            top,
            left + desired.Width,
            top + desired.Height);
    }

    private static (int Width, int Height) CapsulePeekSize(
        DeviceScreenRect bounds,
        double dpiScaleX,
        double dpiScaleY) =>
        (
            Math.Min(
                bounds.Width,
                RoundDevice(
                    CapsulePeekWidthDip *
                    Math.Max(1, dpiScaleX))),
            Math.Min(
                bounds.Height,
                RoundDevice(
                    CapsulePeekHeightDip *
                    Math.Max(1, dpiScaleY))));

    private static bool Contains(
        DeviceScreenRect outer,
        DeviceScreenRect inner) =>
        inner.Left >= outer.Left &&
        inner.Top >= outer.Top &&
        inner.Right <= outer.Right &&
        inner.Bottom <= outer.Bottom;
}
