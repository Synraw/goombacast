using Avalonia.Controls;
using Avalonia.VisualTree;

namespace GoombaCast.Extensions
{
    public static class TextBoxExtensions
    {
        public static void ScrollToEnd(this TextBox? textBox)
        {
            if (textBox == null) return;

            // Set caret to end first
            textBox.CaretIndex = textBox.Text?.Length ?? 0;

            // Try direct parent first
            if (textBox.Parent is ScrollViewer directScroller)
            {
                directScroller.ScrollToEnd();
                return;
            }

            // Otherwise look for ScrollViewer in visual tree
            var scrollViewer = textBox.FindDescendantOfType<ScrollViewer>();
            scrollViewer?.ScrollToEnd();
        }
    }
}