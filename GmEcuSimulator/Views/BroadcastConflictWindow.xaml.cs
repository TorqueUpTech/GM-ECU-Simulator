using System.Windows;

namespace GmEcuSimulator.Views;

// Per-id keep/replace resolver for CAN-id collisions surfaced by a scoped DBC re-import. DataContext
// is a BroadcastConflictViewModel; OK closes with DialogResult=true and the caller reads ReplaceIds.
// Shown from DbcImportWindow (the picker), so Cancel here returns to the picker rather than aborting.
public partial class BroadcastConflictWindow : Window
{
    public BroadcastConflictWindow()
    {
        InitializeComponent();
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
