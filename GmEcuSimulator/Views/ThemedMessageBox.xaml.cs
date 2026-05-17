using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GmEcuSimulator.Views;

// Palette-driven replacement for System.Windows.MessageBox. The dialog itself
// is dumb: caller supplies a list of buttons, each carrying its own callback
// Action. Clicks fire the callback and close the dialog - the message box
// holds no Result state of its own. Severity / chrome / button styling all
// resolve through DynamicResource against the active GMThemeManager palette
// so the dialog stays consistent across runtime theme switches.
public sealed partial class ThemedMessageBox : Window
{
    private ThemedMessageBox(string message, string title, MessageBoxImage image, IReadOnlyList<ThemedDialogButton> buttons)
    {
        InitializeComponent();
        Title = title;
        MessageBlock.Text = message;
        ApplySeverity(image);
        BuildButtons(buttons);
    }

    /// <summary>
    /// Show a modal themed message box. Caller supplies one or more buttons;
    /// each button's <c>OnClick</c> Action fires when picked. The dialog
    /// closes automatically after the callback returns. Pass an empty list
    /// and the dialog falls back to a single dismiss-only "OK".
    /// </summary>
    /// <param name="owner">Owner window for centring + modal semantics. Falls back to <see cref="Application.MainWindow"/>.</param>
    /// <param name="title">Window title shown in the custom titlebar.</param>
    /// <param name="message">Body text. Wraps inside the dialog max-width.</param>
    /// <param name="image">Severity badge style. <see cref="MessageBoxImage.None"/> hides the badge entirely.</param>
    /// <param name="buttons">Button list rendered left-to-right in the footer.</param>
    public static void Show(
        Window? owner,
        string title,
        string message,
        MessageBoxImage image,
        IReadOnlyList<ThemedDialogButton> buttons)
    {
        if (buttons == null || buttons.Count == 0)
            buttons = new[] { new ThemedDialogButton("OK", isDefault: true, isCancel: true) };

        var dlg = new ThemedMessageBox(message, title, image, buttons)
        {
            Owner = owner ?? Application.Current?.MainWindow,
        };
        dlg.ShowDialog();
    }

    /// <inheritdoc cref="Show(Window?, string, string, MessageBoxImage, IReadOnlyList{ThemedDialogButton})" />
    public static void Show(
        Window? owner,
        string title,
        string message,
        MessageBoxImage image,
        params ThemedDialogButton[] buttons)
        => Show(owner, title, message, image, (IReadOnlyList<ThemedDialogButton>)buttons);

    private void ApplySeverity(MessageBoxImage image)
    {
        // None hides the badge entirely so the message text takes the full
        // body width - matches how MessageBox renders without an icon.
        if (image == MessageBoxImage.None)
        {
            SeverityBadge.Visibility = Visibility.Collapsed;
            IconColumn.Width = new GridLength(0);
            return;
        }

        // Map severities onto the theme's status brushes + a single-letter
        // glyph. The badge background uses SetResourceReference so a runtime
        // palette swap recolours it without re-creating the dialog.
        var (brushKey, glyph) = image switch
        {
            MessageBoxImage.Information => ("Status.InfoBrush",    "i"),
            MessageBoxImage.Question    => ("Accent.PrimaryBrush", "?"),
            MessageBoxImage.Warning     => ("Status.WarningBrush", "!"),
            MessageBoxImage.Error       => ("Status.ErrorBrush",   "!"),
            _                           => ("Accent.PrimaryBrush", "i"),
        };
        SeverityBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, brushKey);
        SeverityGlyph.Text = glyph;
    }

    private void BuildButtons(IReadOnlyList<ThemedDialogButton> buttons)
    {
        ButtonRow.Children.Clear();
        // Rendered left-to-right in declaration order; convention is "less
        // destructive on the left, primary on the right". IsDefault binds
        // Enter, IsCancel binds Esc and the chrome close button.
        for (int i = 0; i < buttons.Count; i++)
        {
            var spec = buttons[i];
            var btn = new Button
            {
                Content    = spec.Label,
                MinWidth   = 90,
                Margin     = i == 0 ? new Thickness(0) : new Thickness(8, 0, 0, 0),
                IsDefault  = spec.IsDefault,
                IsCancel   = spec.IsCancel,
                Tag        = spec,
            };
            if (spec.Primary && TryFindResource("Button.Primary") is Style primary)
                btn.Style = primary;
            btn.Click += OnButtonClicked;
            ButtonRow.Children.Add(btn);
        }
    }

    private void OnButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is ThemedDialogButton spec)
            spec.OnClick?.Invoke();
        Close();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        // Chrome close button. Treated as a dismissal - no callback fires
        // unless an IsCancel button is also defined (in which case WPF will
        // already have invoked it via the IsCancel handling above).
        Close();
    }
}

/// <summary>
/// One button on a <see cref="ThemedMessageBox"/> footer. Pairs a visible
/// label with the <see cref="Action"/> the caller wants invoked when the
/// user picks it. The dialog itself is dumb - it just renders the button
/// and routes the click; all state ownership stays with the caller.
/// </summary>
public sealed class ThemedDialogButton
{
    public string Label { get; }
    /// <summary>Invoked on click. <c>null</c> means "just dismiss the dialog".</summary>
    public Action? OnClick { get; }
    /// <summary>True on the button that should fire on Enter.</summary>
    public bool IsDefault { get; }
    /// <summary>True on the button that should fire on Esc / chrome-close.</summary>
    public bool IsCancel { get; }
    /// <summary>Render with the accent-coloured primary button style.</summary>
    public bool Primary { get; }

    public ThemedDialogButton(
        string label,
        Action? onClick = null,
        bool isDefault = false,
        bool isCancel = false,
        bool primary = false)
    {
        Label = label;
        OnClick = onClick;
        IsDefault = isDefault;
        IsCancel = isCancel;
        Primary = primary;
    }
}
