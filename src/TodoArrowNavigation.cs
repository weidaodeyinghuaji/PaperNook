using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace PaperTodo;

// One instance per paper: the desired horizontal position survives focus changes
// and short lines, but is discarded by editing, selection or mouse navigation.
internal sealed class TodoArrowNavigation
{
    private double? _desiredX;
    private bool _moving;
    private TextBox? _placedBox;
    private int _placedLine;

    public void Attach(TextBox box, Func<bool, TextBox?> adjacent)
    {
        var composing = false;
        TextCompositionManager.AddPreviewTextInputStartHandler(box, (_, _) => composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(box, (_, _) => composing = false);
        box.LostKeyboardFocus += (_, _) =>
        {
            composing = false;
            Reset();
        };
        box.SelectionChanged += (_, _) => Reset();
        box.TextChanged += (_, _) => Reset();
        box.SizeChanged += (_, _) => Reset();
        box.PreviewMouseDown += (_, _) => Reset();
        box.PreviewKeyDown += (_, e) =>
        {
            if (Move(box, e.Key, e.IsRepeat, Keyboard.Modifiers, composing, adjacent))
                e.Handled = true;
        };
    }

    private void Reset()
    {
        if (!_moving)
        {
            _desiredX = null;
            _placedBox = null;
        }
    }

    internal bool Move(TextBox box, Key key, bool repeat, ModifierKeys modifiers,
        bool composing, Func<bool, TextBox?> adjacent)
    {
        var vertical = key is Key.Up or Key.Down;
        if (composing || modifiers != ModifierKeys.None || box.SelectionLength != 0 ||
            (!vertical && key is not (Key.Left or Key.Right)))
        {
            Reset();
            return false;
        }

        if (!vertical) Reset();
        box.UpdateLayout();
        if (box.LineCount < 1) return false;
        var backward = key is Key.Up or Key.Left;
        var line = CaretLine(box);
        var boundary = key switch
        {
            Key.Up => line == 0,
            Key.Down => line == box.LineCount - 1,
            Key.Left => box.CaretIndex == 0,
            _ => box.CaretIndex == box.Text.Length
        };

        // Auto-repeat may move inside a todo, but can never cross its boundary.
        // Only the initial key-down of a fresh physical press may cross.
        if (boundary && repeat) return true;
        if (!vertical && !boundary) return false;

        var target = boundary ? adjacent(backward) : box;
        if (target == null) return true; // No wrapping at either end of the list.
        target.UpdateLayout();
        if (target.LineCount < 1) return true;
        if (vertical)
        {
            var caret = CaretRect(box, line);
            if (caret.IsEmpty) return false;
            _desiredX ??= box.PointToScreen(new Point(caret.X, caret.Y)).X;
        }

        _moving = true;
        try
        {
            if (!target.IsKeyboardFocused && !target.Focus()) return true;
            if (vertical)
            {
                var targetLine = boundary
                    ? (backward ? target.LineCount - 1 : 0)
                    : line + (backward ? -1 : 1);
                PlaceOnLine(target, targetLine, _desiredX!.Value);
            }
            else
            {
                target.CaretIndex = backward ? target.Text.Length : 0;
            }
            var rect = CaretRect(target, CaretLine(target));
            if (!rect.IsEmpty) target.BringIntoView(rect);
        }
        finally
        {
            _moving = false;
        }
        return true;
    }

    private int CaretLine(TextBox box)
    {
        var line = Math.Clamp(box.GetLineIndexFromCharacterIndex(box.CaretIndex), 0, box.LineCount - 1);
        if (ReferenceEquals(_placedBox, box) && _placedLine >= 0 && _placedLine < box.LineCount)
            return _placedLine;

        // Only a soft-wrap boundary has two caret positions for the same index.
        // The character API always returns the leading edge; WPF publishes the
        // actual caret to Win32 for accessibility, including its trailing affinity.
        if (line > 0 && box.CaretIndex > 0 &&
            box.CaretIndex == box.GetCharacterIndexFromLineIndex(line) &&
            box.Text[box.CaretIndex - 1] is not ('\r' or '\n') &&
            box.IsKeyboardFocused && PresentationSource.FromVisual(box) is HwndSource source)
        {
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            if (GetGUIThreadInfo(0, ref info) && info.CaretWindow == source.Handle)
            {
                var point = new NativePoint { X = info.Left, Y = info.Top };
                if (ClientToScreen(source.Handle, ref point))
                {
                    var actual = box.PointFromScreen(new Point(point.X, point.Y));
                    var trailing = box.GetRectFromCharacterIndex(box.CaretIndex - 1, true);
                    var leading = box.GetRectFromCharacterIndex(box.CaretIndex);
                    if (!trailing.IsEmpty && !leading.IsEmpty &&
                        Math.Abs(actual.Y - trailing.Top) < Math.Abs(actual.Y - leading.Top))
                        return line - 1;
                }
            }
        }
        return line;
    }

    private static Rect CaretRect(TextBox box, int line)
    {
        return box.CaretIndex > 0 && box.GetLineIndexFromCharacterIndex(box.CaretIndex) > line
            ? box.GetRectFromCharacterIndex(box.CaretIndex - 1, true)
            : box.GetRectFromCharacterIndex(box.CaretIndex);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size, Flags;
        public IntPtr ActiveWindow, FocusWindow, CaptureWindow, MenuOwner, MoveSizeWindow, CaretWindow;
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

    private void PlaceOnLine(TextBox box, int line, double screenX)
    {
        if (line < 0 || line >= box.LineCount) return;
        var start = box.GetCharacterIndexFromLineIndex(line);
        var end = Math.Min(box.Text.Length, start + box.GetLineLength(line));
        while (end > start && box.Text[end - 1] is '\r' or '\n') end--;
        var x = box.PointFromScreen(new Point(screenX, 0)).X;
        var best = start;
        var distance = double.PositiveInfinity;
        // Hit testing returns a character, not the nearest insertion edge. Compare
        // actual glyph edges, respecting surrogate pairs and combining sequences.
        foreach (var offset in StringInfo.ParseCombiningCharacters(box.Text[start..end]).Append(end - start))
        {
            var index = start + offset;
            var rect = index == end && end > start
                ? box.GetRectFromCharacterIndex(end - 1, true)
                : box.GetRectFromCharacterIndex(index);
            if (rect.IsEmpty) continue;
            var candidate = Math.Abs(rect.X - x);
            if (candidate < distance)
            {
                distance = candidate;
                best = index;
            }
        }
        box.CaretIndex = best;
        if (best == end && end > start)
        {
            // Preserve the preceding visual line at a soft wrap.
            box.CaretIndex = start;
            EditingCommands.MoveToLineEnd.Execute(null, box);
        }
        _placedBox = box;
        _placedLine = line;
    }
}
