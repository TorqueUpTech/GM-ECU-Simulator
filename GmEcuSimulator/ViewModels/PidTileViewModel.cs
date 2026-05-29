using System.ComponentModel;

namespace GmEcuSimulator.ViewModels;

// One tile on the main window's live-PID dashboard. Hosts EITHER an editable
// PID row ($22/$2D/$1A, a PidViewModel) OR a built-in OBD-II $01 PID (a
// J1979RowViewModel) - the two value layers the simulator exposes. The tile
// presents a uniform face (EcuName / FriendlyName / LiveValue / Unit) so the
// DataTemplate is source-agnostic.
//
// The underlying source VM raises PropertyChanged on its own value; the tile
// forwards that to its uniform properties so the bound tile updates on the
// 10 Hz refresh. Ecu / source are re-bindable (and Unhook detaches the
// handlers) so MainViewModel can re-resolve tiles against freshly-rebuilt VM
// instances after a config load / editor close.
public sealed class PidTileViewModel : NotifyPropertyChangedBase
{
    private EcuViewModel ecu;
    private PidViewModel? pid;
    private J1979RowViewModel? obd2;

    private PidTileViewModel(EcuViewModel ecu, Action<PidTileViewModel> remove)
    {
        this.ecu = ecu;
        RemoveCommand = new RelayCommand(() => remove(this));
        ecu.PropertyChanged += OnEcuChanged;
    }

    public PidTileViewModel(EcuViewModel ecu, PidViewModel pid, Action<PidTileViewModel> remove)
        : this(ecu, remove)
    {
        this.pid = pid;
        pid.PropertyChanged += OnSourceChanged;
    }

    public PidTileViewModel(EcuViewModel ecu, J1979RowViewModel obd2, Action<PidTileViewModel> remove)
        : this(ecu, remove)
    {
        this.obd2 = obd2;
        obd2.PropertyChanged += OnSourceChanged;
    }

    // Bound by the tile's right-click context menu's single "Delete" item.
    public RelayCommand RemoveCommand { get; }

    public EcuViewModel Ecu => ecu;
    internal PidViewModel? Pid => pid;
    internal J1979RowViewModel? Obd2 => obd2;

    // ---- Uniform tile face (what the DataTemplate binds to) ----
    public string EcuName => ecu.Name;
    public string FriendlyName => pid?.Name ?? obd2?.Name ?? "";
    // $01 rows fold the unit into the decoded string (e.g. "850 rpm"), so the
    // separate Unit slot is left empty for them.
    public string LiveValue => pid?.LiveValue ?? obd2?.Decoded ?? "-";
    public string Unit => pid?.Unit ?? "";

    // Sample the underlying source at the given bus time. Called from the main
    // window's 10 Hz tick so a pinned $01 row (which the inspector's per-ECU
    // loop doesn't otherwise refresh) stays live.
    public void RefreshLive(double timeMs)
    {
        pid?.RefreshLive(timeMs);
        obd2?.RefreshLive(timeMs);
    }

    // Re-point this tile at freshly-rebuilt VM instances (config reload / Load
    // PIDs). Exactly one of newPid / newObd2 is non-null, matching the tile's
    // original source kind.
    internal void Rebind(EcuViewModel newEcu, PidViewModel? newPid, J1979RowViewModel? newObd2)
    {
        if (!ReferenceEquals(ecu, newEcu))
        {
            ecu.PropertyChanged -= OnEcuChanged;
            ecu = newEcu;
            ecu.PropertyChanged += OnEcuChanged;
        }
        if (!ReferenceEquals(pid, newPid))
        {
            if (pid != null) pid.PropertyChanged -= OnSourceChanged;
            pid = newPid;
            if (pid != null) pid.PropertyChanged += OnSourceChanged;
        }
        if (!ReferenceEquals(obd2, newObd2))
        {
            if (obd2 != null) obd2.PropertyChanged -= OnSourceChanged;
            obd2 = newObd2;
            if (obd2 != null) obd2.PropertyChanged += OnSourceChanged;
        }
        OnPropertyChanged(nameof(EcuName));
        OnPropertyChanged(nameof(FriendlyName));
        OnPropertyChanged(nameof(LiveValue));
        OnPropertyChanged(nameof(Unit));
    }

    // Detach all forwarded subscriptions. Called when the tile is dropped from
    // the dashboard so it doesn't dangle off the (longer-lived) source VMs.
    internal void Unhook()
    {
        ecu.PropertyChanged -= OnEcuChanged;
        if (pid != null) pid.PropertyChanged -= OnSourceChanged;
        if (obd2 != null) obd2.PropertyChanged -= OnSourceChanged;
    }

    private void OnEcuChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EcuViewModel.Name)) OnPropertyChanged(nameof(EcuName));
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PidViewModel.LiveValue):
            case nameof(J1979RowViewModel.Decoded):
                OnPropertyChanged(nameof(LiveValue));
                break;
            case nameof(PidViewModel.Name):
                OnPropertyChanged(nameof(FriendlyName));
                break;
            case nameof(PidViewModel.Unit):
                OnPropertyChanged(nameof(Unit));
                break;
        }
    }
}
