using Common.Signals;
using Core.Ecu;

namespace GmEcuSimulator.ViewModels;

// One read-only row in the editor's "$01 (OBD-II)" section: a J1979 catalogue PID the ECU can answer. The encoding is
// legislated, so the only editable thing is Supported - whether this ECU advertises the PID (which drives the
// $00/$20 support bitmask). Live shows the current wire bytes, refreshed off the main window's timer.
public sealed class J1979RowViewModel : NotifyPropertyChangedBase
{
    private readonly J1979Pid def;
    private readonly EcuNode node;

    public J1979RowViewModel(J1979Pid def, EcuNode node)
    {
        this.def = def;
        this.node = node;
    }

    public string PidHex => $"${def.Pid:X2}";
    public string Name => def.Name;
    public int Bytes => def.Length;

    // The raw 1-byte $01 PID id. Used as the persistence/resolve key for an
    // $01 live-tile (the dashboard pins these by id).
    public byte Pid => def.Pid;

    // Two-way: flips this PID in the ECU's advertised $01 subset. Reads/writes EcuNode.Mode1Supported, so unchecking
    // it makes a tester's $01 request for this PID return NRC $31 and clears its bit in the support mask.
    public bool Supported
    {
        get => node.Mode1Supported.Contains(def.Pid);
        set
        {
            if (node.Mode1Supported.Contains(def.Pid) == value) return;
            node.SetMode1Supported(def.Pid, value);
            OnPropertyChanged();
        }
    }

    private string rawHex = "--";
    public string RawHex
    {
        get => rawHex;
        private set => SetField(ref rawHex, value);
    }

    private string decoded = "--";
    public string Decoded
    {
        get => decoded;
        private set => SetField(ref decoded, value);
    }

    // Encode the PID at the current bus time, then show both the raw wire bytes (hex) and the decoded value(s).
    // Compound PIDs (e.g. an O2 sensor) decode to a composite string. Called from the editor's refresh timer.
    public void RefreshLive(double timeMs)
    {
        var buf = new byte[def.Length];
        def.Encode(node.EngineModel, node.DiscreteState, timeMs, buf);
        RawHex = Convert.ToHexString(buf);
        Decoded = def.Decode(buf);
    }
}
