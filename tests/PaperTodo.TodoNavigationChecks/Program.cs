using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Threading;
using PaperTodo;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var panel = new StackPanel();
        var boxes = new[] { Box("WWWWiiiiWWWW"), Box("短"), Box("WWWWiiiiWWWW"), Box("第一行\n第二行\n第三行") };
        var navigation = new TodoArrowNavigation();
        for (var i = 0; i < boxes.Length; i++)
        {
            var index = i;
            panel.Children.Add(boxes[i]);
            navigation.Attach(boxes[i], back => Neighbor(index, back));
        }
        TextBox? Neighbor(int index, bool back) => boxes.ElementAtOrDefault(index + (back ? -1 : 1));
        var window = new Window { Content = panel, Width = 350, Height = 400, ShowInTaskbar = false };
        window.Show();
        window.UpdateLayout();
        bool Move(int i, Key key, bool repeat = false, ModifierKeys modifiers = ModifierKeys.None, bool composing = false)
            => navigation.Move(boxes[i], key, repeat, modifiers, composing, back => Neighbor(i, back));
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            Console.WriteLine("PASS: " + message);
        }
        boxes[0].Focus();
        boxes[0].CaretIndex = 9;
        Move(0, Key.Down);
        Check(boxes[1].IsKeyboardFocused && boxes[1].CaretIndex == 1, "Short row clamps to end");
        Move(1, Key.Down, true);
        Check(boxes[1].IsKeyboardFocused, "Held Down cannot cross another todo");
        Move(1, Key.Down);
        Check(boxes[2].IsKeyboardFocused && boxes[2].CaretIndex == 9, "Desired X survives short row");
        Move(2, Key.Up);
        Move(1, Key.Up);
        Check(boxes[0].CaretIndex == 9, "Up restores the same X");
        boxes[3].Focus();
        boxes[3].CaretIndex = 6;
        Move(3, Key.Up, true);
        Check(boxes[3].IsKeyboardFocused && boxes[3].GetLineIndexFromCharacterIndex(boxes[3].CaretIndex) == 0, "Repeat moves within todo");
        Move(3, Key.Up, true);
        Check(boxes[3].IsKeyboardFocused, "Arrival at boundary stops repeat");
        Move(3, Key.Up);
        Check(boxes[2].IsKeyboardFocused, "Fresh Up crosses boundary");
        boxes[2].CaretIndex = 0;
        Move(2, Key.Left);
        Check(boxes[1].IsKeyboardFocused && boxes[1].CaretIndex == 1, "Left enters previous text end");
        Move(1, Key.Right, true);
        Check(boxes[1].IsKeyboardFocused, "Repeat Right stops at boundary");
        Move(1, Key.Right);
        Check(boxes[2].IsKeyboardFocused && boxes[2].CaretIndex == 0, "Right enters next text start");
        boxes[2].SelectAll();
        Check(!Move(2, Key.Down), "Text selection retains native handling");
        boxes[2].CaretIndex = 0;
        Check(!Move(2, Key.Up, modifiers: ModifierKeys.Shift), "Shift retains native handling");
        Check(!Move(2, Key.Up, composing: true), "IME composition retains native handling");
        boxes[0].Focus();
        boxes[0].CaretIndex = 0;
        Move(0, Key.Up);
        Check(boxes[0].IsKeyboardFocused, "First todo does not wrap");
        boxes[1].Text = "";
        boxes[0].CaretIndex = 9;
        Move(0, Key.Down);
        Move(1, Key.Down);
        Check(boxes[2].CaretIndex == 9, "Empty todo preserves desired X");
        boxes[1].Text = "abcdefghij abcdefghij abcdefghij";
        boxes[1].Width = 65;
        window.UpdateLayout();
        Check(boxes[1].LineCount > 2, "Fixture has automatic wrapping");
        boxes[0].Focus();
        boxes[0].CaretIndex = boxes[0].Text.Length;
        Move(0, Key.Down);
        var firstEnd = boxes[1].CaretIndex;
        Check(firstEnd == boxes[1].GetCharacterIndexFromLineIndex(1), "Short wrapped target lands at first visual line end");
        Move(1, Key.Down, true);
        Check(boxes[1].IsKeyboardFocused && boxes[1].CaretIndex > firstEnd, "Repeat continues inside wrapped target");
        for (var i = 0; i < boxes[1].LineCount + 2; i++) Move(1, Key.Down, true);
        Check(boxes[1].IsKeyboardFocused, "Wrapped final line blocks held Down");
        Move(1, Key.Down);
        Check(boxes[2].IsKeyboardFocused && boxes[2].CaretIndex == boxes[2].Text.Length, "Wrapped short lines preserve original X");
        // Layout invalidates the cached visual line without changing the text/caret.
        boxes[1].Focus();
        boxes[1].CaretIndex = 0;
        for (var i = 0; i < boxes[1].LineCount - 1; i++) Move(1, Key.Down);
        boxes[1].Width = 250;
        window.UpdateLayout();
        Move(1, Key.Down);
        Check(boxes[1].IsKeyboardFocused || boxes[2].IsKeyboardFocused, "Resize followed by Down does not use an out-of-range line");

        boxes[1].Width = 65;
        window.UpdateLayout();
        boxes[1].Focus();
        boxes[1].CaretIndex = 0;
        EditingCommands.MoveToLineEnd.Execute(null, boxes[1]);
        Pump();
        var nativeEnd = boxes[1].CaretIndex;
        Move(1, Key.Down);
        Check(boxes[1].IsKeyboardFocused && boxes[1].CaretIndex == boxes[1].GetCharacterIndexFromLineIndex(2), "Native End then Down moves exactly one visual line at the same X");
        boxes[1].CaretIndex = 0;
        EditingCommands.MoveToLineEnd.Execute(null, boxes[1]);
        Pump();
        Move(1, Key.Up);
        Check(boxes[0].IsKeyboardFocused && boxes[0].CaretIndex > 0, "Native End then Up crosses from first line with trailing X");
        boxes[1].Focus();
        boxes[1].CaretIndex = nativeEnd;
        Pump();
        Move(1, Key.Up);
        Check(boxes[1].IsKeyboardFocused && boxes[1].CaretIndex == 0, "Leading side of same wrap index stays in todo");
        window.Close();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static TextBox Box(string text) => new()
    {
        Text = text, FontSize = 18, TextWrapping = TextWrapping.Wrap,
        AcceptsReturn = true, Margin = new Thickness(10), Width = 250
    };
}
