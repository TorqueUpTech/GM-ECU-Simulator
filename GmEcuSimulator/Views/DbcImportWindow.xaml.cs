using System.Windows;

namespace GmEcuSimulator.Views;

// Scoped DBC-import picker. DataContext is a DbcImportViewModel; Import closes with DialogResult=true
// so the caller (MainViewModel.ImportDbc) reads the picked messages and apply-mode off the VM.
public partial class DbcImportWindow : Window
{
    public DbcImportWindow()
    {
        InitializeComponent();
    }

    private void OnImportClicked(object sender, RoutedEventArgs e)
    {
        var vm = DataContext as ViewModels.DbcImportViewModel;

        // 0. Reconcile with no net change (picks equal the matched baseline, e.g. nothing ticked and
        // nothing matched) - there's nothing to apply, so don't bother the user with the Append/
        // Overwrite or collision prompts. Close through; MainViewModel.ImportDbc reports "no changes".
        if (vm is { ReconcileMode: true, HasPendingChanges: false })
        {
            DialogResult = true;
            Close();
            return;
        }

        // 1. When the table has rows this DBC doesn't define, Append (keep them) vs Overwrite (drop
        // them, take only the picks) is ambiguous - ask here, while the picker is still open, so
        // Cancel just returns to it instead of aborting the whole import.
        if (vm is { HasOrphans: true })
        {
            bool decided = false;
            ThemedMessageBox.Show(
                this,
                "Import DBC",
                $"This ECU has {vm.ExistingCount} broadcast message(s), some of which this DBC does not " +
                "define.\n\nAppend - keep those rows and fold in your picks.\n" +
                "Overwrite - clear the table and load only the messages you picked.",
                MessageBoxImage.Question,
                new ThemedDialogButton("Cancel", isCancel: true),
                new ThemedDialogButton("Append", onClick: () => { decided = true; vm.ApplyMode = ViewModels.BroadcastImportMode.Append; }),
                new ThemedDialogButton("Overwrite", onClick: () => { decided = true; vm.ApplyMode = ViewModels.BroadcastImportMode.Overwrite; }, isDefault: true, primary: true));
            if (!decided) return;   // Cancel / chrome-close: stay on the picker.
        }

        // 2. Append only: a ticked id can already exist in the table with a DIFFERENT shape (a DBC
        // reusing the arbitration id for an unrelated message). One id carries one frame, so resolve
        // each clash keep/replace before committing. Overwrite skips this - it clears the table first.
        if (vm is not null && vm.ApplyMode == ViewModels.BroadcastImportMode.Append)
        {
            var collisions = vm.DetectCollisions();
            if (collisions.Count > 0)
            {
                var conflictVm = new ViewModels.BroadcastConflictViewModel(collisions);
                var win = new BroadcastConflictWindow { DataContext = conflictVm, Owner = this };
                if (win.ShowDialog() != true) return;   // Cancel: stay on the picker.
                vm.ReplaceIds = conflictVm.ReplaceIds;
            }
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
