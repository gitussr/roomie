using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Roomie.Core.Messaging;

namespace Roomie.App.Views;

/// <summary>
/// Shows a message's text with its web addresses as links. Clicking one opens it in the browser, and
/// right-clicking offers to copy it. What counts as a link, and why, is in <see cref="MessageLinks"/>.
/// </summary>
public static class MessageText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(MessageText), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBlock block)
        {
            return;
        }

        var text = e.NewValue as string ?? string.Empty;
        block.Inlines.Clear();
        foreach (var segment in MessageLinks.Split(text))
        {
            block.Inlines.Add(segment.Address is { } address ? MakeLink(segment.Text, address) : new Run(segment.Text));
        }

        block.ContextMenu = Menu(("Copy message", () => Copy(text)));
    }

    private static Hyperlink MakeLink(string written, string address)
    {
        var link = new Hyperlink(new Run(written))
        {
            ToolTip = "Opens " + address,
            Cursor = Cursors.Hand,
            ContextMenu = Menu(("Open link", () => Open(address)), ("Copy link", () => Copy(address))),
        };
        link.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        link.Click += (_, _) => Open(address);
        return link;
    }

    private static ContextMenu Menu(params (string Header, Action Run)[] items)
    {
        var menu = new ContextMenu();
        foreach (var (header, run) in items)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }

        return menu;
    }

    private static void Open(string address)
    {
        // Checked again here, so only a web page ever reaches Windows, whatever led to this.
        if (MessageLinks.ToAddress(address) is not { } safe)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(safe) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show("Windows couldn't open the link. You can right-click it and copy it instead.", "Roomie");
        }
    }

    private static void Copy(string text)
    {
        // Another program can be holding the clipboard for a moment. Trying again is up to the user.
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
        }
    }
}
