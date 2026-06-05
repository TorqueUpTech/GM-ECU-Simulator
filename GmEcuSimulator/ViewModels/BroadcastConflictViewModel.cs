using System.Collections.ObjectModel;

namespace GmEcuSimulator.ViewModels;

// Backs the post-import collision dialog. A scoped DBC re-import (Append mode) can pick a CAN id that
// already exists in the table with a different shape - the same arbitration id reused by another DBC
// for an unrelated message. One id can carry only one frame, so the user resolves each clash here:
// Replace (take the imported definition, discard the existing row + its mappings) or Keep (leave the
// existing row, ignore the import for that id). Defaults to Replace - "the DBC I just opened wins".
public sealed class BroadcastConflictViewModel
{
    public ObservableCollection<ConflictRow> Conflicts { get; } = new();

    public BroadcastConflictViewModel(IEnumerable<BroadcastCollision> collisions)
    {
        foreach (var c in collisions) Conflicts.Add(new ConflictRow(c));
    }

    // The ids the user chose to overwrite with the imported definition. Unchecked rows (Keep) are
    // absent, so Reconcile leaves their existing rows untouched.
    public IReadOnlySet<uint> ReplaceIds => Conflicts.Where(r => r.Replace).Select(r => r.CanId).ToHashSet();
}

public sealed class ConflictRow : NotifyPropertyChangedBase
{
    private readonly BroadcastCollision collision;

    public ConflictRow(BroadcastCollision collision)
    {
        this.collision = collision;
        replace = true;   // default: the freshly imported DBC wins.
    }

    public uint CanId => collision.CanId;
    public string CanIdHex => collision.CanIdHex;
    public string ExistingLabel => collision.ExistingLabel;
    public string IncomingLabel => collision.IncomingLabel;

    private bool replace;
    public bool Replace
    {
        get => replace;
        set => SetField(ref replace, value);
    }
}
