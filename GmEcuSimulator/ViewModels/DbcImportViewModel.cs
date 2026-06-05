using Common.Dbc;
using Core.Dbc;
using Core.Ecu;
using System.Collections.ObjectModel;

namespace GmEcuSimulator.ViewModels;

// Backs the scoped DBC-import dialog. A DBC describes the whole bus, so the user first picks the
// transmitting module, then ticks which of its messages to import; messages carrying an
// auto-mappable live signal are pre-ticked.
//
// Two pre-tick modes:
//   * Fresh import (the ECU has no broadcasts yet): pre-tick messages that carry an auto-mappable
//     live signal - the "interesting" candidates.
//   * Reconcile (the ECU already has broadcasts): suppress the candidate auto-tick; instead pre-tick
//     only messages that MATCH an existing row - same CAN id AND same shape (a true re-import of the
//     same message). A same-id/different-shape message is a collision, not a match: it stays unticked
//     and the existing row is left alone unless the user explicitly ticks it (-> keep/replace dialog).
//     The caller treats a de-ticked matched id as "remove that row" and a newly ticked id as "append",
//     leaving rows this DBC doesn't define untouched. See MainViewModel.ImportDbc.
public sealed class DbcImportViewModel : NotifyPropertyChangedBase
{
    private readonly DbcDatabase db;
    private readonly IReadOnlyList<BroadcastMessage> existing;
    private readonly IReadOnlySet<uint> existingCanIds;
    private readonly IReadOnlyDictionary<uint, BroadcastMessage> existingById;

    public string FileName { get; }
    public IReadOnlyList<TransmitterOption> Transmitters { get; }
    public ObservableCollection<DbcMessageRow> Messages { get; } = new();

    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }

    // True when the ECU already has broadcast rows -> reconcile pre-tick + reconcile commit.
    public bool ReconcileMode => existingCanIds.Count > 0;

    // How OK should fold the picks into an ECU that already has broadcasts. Resolved by the window
    // (it asks only when orphan rows exist; otherwise Append); read by MainViewModel.ImportDbc.
    public BroadcastImportMode ApplyMode { get; set; } = BroadcastImportMode.Append;

    // The table holds rows this DBC view doesn't define (orphans). Then Append (keep them) vs
    // Overwrite (drop them and take only the picks) is ambiguous, so the window must ask before it
    // commits. Scoped to the current transmitter view, like the reconcile itself.
    public bool HasOrphans => existingCanIds.Count > 0 && existingCanIds.Any(id => !DisplayedIds.Contains(id));

    // Existing broadcast row count - the orphan prompt quotes it.
    public int ExistingCount => existingCanIds.Count;

    // In reconcile mode the pre-ticked matched set (same id + same shape) is the "no-op" baseline:
    // re-importing exactly those rows keeps them as-is. So the picks represent a change only when they
    // differ from MatchedIds - a de-ticked match removes a row, a newly ticked id adds or replaces one.
    // Equal sets (including both empty) mean nothing to apply, so OK can skip the Append/Overwrite and
    // collision prompts and just close (MainViewModel.ImportDbc then reports "no changes").
    public bool HasPendingChanges => !MatchedIds.SetEquals(SelectedIds);

    // Ticked ids the user chose to overwrite with the imported definition, resolved by the collision
    // dialog. Empty unless DetectCollisions found same-id/different-shape clashes and the user picked
    // Replace for some of them. Read by MainViewModel.ImportDbc -> Reconcile.
    public IReadOnlySet<uint> ReplaceIds { get; set; } = new HashSet<uint>();

    // Existing rows this DBC view re-defines with an IDENTICAL shape (same CAN id + same DLC/signal
    // layout) - a true re-import of the same message. These are the only ids pre-ticked in reconcile
    // mode, and the only ids a de-tick removes. A same-id/different-shape row is a collision, NOT a
    // match (see DetectCollisions). Scoped to the current transmitter view, like DisplayedIds.
    public HashSet<uint> MatchedIds
    {
        get
        {
            var set = new HashSet<uint>();
            if (SelectedTransmitter is null) return set;
            foreach (var m in db.Messages.Where(m => m.Transmitter == SelectedTransmitter.Name))
                if (existingById.TryGetValue(m.Id, out var ex) && DbcImporter.SameShape(ex, DbcImporter.ToBroadcast(m)))
                    set.Add(m.Id);
            return set;
        }
    }

    // Ticked ids that already exist in the table but whose imported shape differs from the current
    // row (a different DBC reusing the arbitration id for an unrelated message). Append-commit can't
    // hold both, so the window asks keep/replace per collision before closing. Empty -> commit straight
    // through. Only meaningful in Append mode (Overwrite clears the table, so nothing collides).
    public List<BroadcastCollision> DetectCollisions()
    {
        var clashes = new List<BroadcastCollision>();
        foreach (var inc in BuildSelected())
            if (existingById.TryGetValue(inc.CanId, out var ex) && !DbcImporter.SameShape(ex, inc))
                clashes.Add(new BroadcastCollision(ex, inc));
        return clashes;
    }

    public DbcImportViewModel(DbcDatabase db, string fileName, IReadOnlyList<BroadcastMessage>? existing = null)
    {
        this.db = db;
        this.existing = existing ?? Array.Empty<BroadcastMessage>();
        this.existingCanIds = this.existing.Select(b => b.CanId).ToHashSet();
        this.existingById = this.existing
            .GroupBy(b => b.CanId)
            .ToDictionary(g => g.Key, g => g.First());   // CAN id is unique per ECU; guard dup ids anyway
        FileName = fileName;
        Transmitters = DbcImporter.TransmittersByMessageCount(db)
            .Select(t => new TransmitterOption(t.Transmitter, t.Count))
            .ToList();
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        selectedTransmitter = Transmitters.FirstOrDefault();
        RebuildMessages();
    }

    private TransmitterOption? selectedTransmitter;
    public TransmitterOption? SelectedTransmitter
    {
        get => selectedTransmitter;
        set { if (SetField(ref selectedTransmitter, value)) RebuildMessages(); }
    }

    public int SelectedCount => Messages.Count(m => m.Selected);

    private void RebuildMessages()
    {
        Messages.Clear();
        if (SelectedTransmitter is null) { OnPropertyChanged(nameof(SelectedCount)); return; }
        var matched = MatchedIds;   // same id + same shape; computed once for this transmitter view
        foreach (var m in db.Messages.Where(m => m.Transmitter == SelectedTransmitter.Name).OrderBy(m => m.Id))
        {
            // Reconcile: pre-tick only a true match (same id AND shape). A same-id/different-shape
            // collision stays unticked. Fresh import: pre-tick the auto-mappable candidates.
            bool preTick = ReconcileMode ? matched.Contains(m.Id) : DbcImporter.HasMappableSignal(m);
            var row = new DbcMessageRow(m, preTick, MappedHint(m));
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DbcMessageRow.Selected)) OnPropertyChanged(nameof(SelectedCount)); };
            Messages.Add(row);
        }
        OnPropertyChanged(nameof(SelectedCount));
    }

    // "live: RPM, ECT" hint - the live signals the importer would auto-map for this message.
    private static string MappedHint(DbcMessage m)
    {
        var mapped = m.Signals.Select(DbcImporter.AutoMap).Where(s => s is not null).Select(s => s!.Value.ToString()).ToList();
        return mapped.Count == 0 ? "" : "live: " + string.Join(", ", mapped);
    }

    private void SetAll(bool value)
    {
        foreach (var m in Messages) m.Selected = value;
        OnPropertyChanged(nameof(SelectedCount));
    }

    // The picked messages converted to runtime broadcast messages (auto-mapped).
    public List<BroadcastMessage> BuildSelected()
    {
        if (SelectedTransmitter is null) return new();
        return DbcImporter.ToBroadcasts(db, SelectedTransmitter.Name, SelectedIds);
    }

    // Every CAN id the picker is currently showing (the chosen transmitter's messages) - the ids the
    // user had a chance to tick/untick. Reconcile uses this to scope removals to ids that are actually
    // in this DBC view; a table row whose id isn't here is left alone.
    public HashSet<uint> DisplayedIds => Messages.Select(m => m.Id).ToHashSet();

    // The ticked CAN ids in the current view.
    public HashSet<uint> SelectedIds => Messages.Where(m => m.Selected).Select(m => m.Id).ToHashSet();
}

// How a scoped re-import folds into an ECU that already has broadcasts.
public enum BroadcastImportMode
{
    Append,     // Reconcile: keep rows this DBC doesn't define, drop de-ticked, add newly ticked.
    Overwrite,  // Clear the whole table, load only the picked messages.
}

// A ticked CAN id that already exists in the table but whose imported definition has a different
// shape (a different DBC reusing the arbitration id). The collision dialog shows Existing vs Incoming
// so the user can pick keep or replace.
public sealed record BroadcastCollision(BroadcastMessage Existing, BroadcastMessage Incoming)
{
    public uint CanId => Existing.CanId;
    public string CanIdHex => $"0x{CanId:X3}";
    public string ExistingLabel => Describe(Existing);
    public string IncomingLabel => Describe(Incoming);
    private static string Describe(BroadcastMessage m)
        => $"{m.Name}  ({m.Dlc}B, {m.Signals.Count} sig)";
}

public sealed record TransmitterOption(string Name, int Count)
{
    public string Display => $"{Name}  ({Count} msgs)";
}

public sealed class DbcMessageRow : NotifyPropertyChangedBase
{
    public uint Id { get; }
    public string Display { get; }
    public string Hint { get; }

    public DbcMessageRow(DbcMessage m, bool preTick, string hint)
    {
        Id = m.Id;
        Display = $"0x{m.Id:X3}  {m.Name}";
        Hint = hint;
        selected = preTick;
    }

    private bool selected;
    public bool Selected
    {
        get => selected;
        set => SetField(ref selected, value);
    }
}
