using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Teleporter.Views;

internal static class Dialogs
{
    /// <summary>Modal yes/no. Returns true only on the confirm button.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string confirmText, bool danger = false)
    {
        bool result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var confirm = new Button { Content = confirmText, Classes = { danger ? "danger" : "accent" } };
        var cancel = new Button { Content = "Cancel" };
        confirm.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, confirm },
                },
            },
        };
        await dialog.ShowDialog(owner);
        return result;
    }
}
