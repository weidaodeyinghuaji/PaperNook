using System.Windows.Media;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleHost
{
    public void PulseReminder()
    {
        if (_disposed || !Window.IsVisible)
        {
            return;
        }

        AnimationHelper.QuickBounce(
            VisualSurface,
            scale: 1.055,
            duration: 95);
        if (Theme.DangerBrush is SolidColorBrush danger)
        {
            AnimationHelper.FlashHighlight(
                Chrome,
                danger.Color,
                duration: 130);
        }
    }
}
