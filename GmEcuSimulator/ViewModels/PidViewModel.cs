using Common.Protocol;
using Common.Signals;
using Common.Waveforms;
using Core.Ecu;

namespace GmEcuSimulator.ViewModels;

// Editable view of one Pid. Address / Name / Size / DataType / scaling /
// unit are pushed straight into the model. Address changes trigger the
// owning EcuViewModel to update its lookup so subsequent $22 requests
// route to the right entry.
public sealed class PidViewModel : NotifyPropertyChangedBase
{
    public Pid Model { get; }
    private readonly EcuViewModel parent;
    private string liveValue = "-";

    private bool hasAliasCollision;
    private string? aliasCollisionTooltip;

    public PidViewModel(Pid pid, EcuViewModel parent)
    {
        Model = pid;
        this.parent = parent;
        lengthBytesText = Model.ResponseLength.ToString();
        Waveform = new WaveformViewModel(pid);

        // Re-evaluate the aliasing warning whenever the user changes the
        // waveform's frequency or shape - those are the only inputs that
        // affect Nyquist analysis. Other waveform tweaks (amplitude, offset,
        // phase, file path) don't change whether sampling will alias.
        Waveform.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WaveformViewModel.FrequencyHz)
             || e.PropertyName == nameof(WaveformViewModel.Shape))
            {
                RaiseAliasWarningChanged();
            }
        };
    }

    // Re-announce the Nyquist-aliasing warning state. Anything that changes whether the row actually USES its
    // waveform (signal source picked, static bytes set) or its frequency/shape must call this so the row's red-border
    // warning clears/appears immediately.
    private void RaiseAliasWarningChanged()
    {
        OnPropertyChanged(nameof(AliasWarning));
        OnPropertyChanged(nameof(AliasWarningTooltip));
        OnPropertyChanged(nameof(HasAliasWarning));
        OnPropertyChanged(nameof(HasWarning));
    }

    public WaveformViewModel Waveform { get; }

    public uint Address
    {
        get => Model.Address;
        set
        {
            if (Model.Address == value) return;
            var oldAddress = Model.Address;
            Model.Address = value;
            // The per-mode store is keyed by Address, so re-key the entry before notifying. Without this a $2D / $22
            // read against the new address misses the dict and the ECU NRCs RequestOutOfRange.
            parent.OnPidAddressChanged(Model, oldAddress);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AddressHex));
            OnPropertyChanged(nameof(IdentifierLabel));
            parent.RaisePidsChanged();
        }
    }

    public PidMode Mode
    {
        get => Model.Mode;
        set
        {
            if (Model.Mode == value) return;
            var oldMode = Model.Mode;
            Model.Mode = value;
            // The underlying Pid moves between the per-mode stores ($1A / $22 / $2D); EcuNode.RelocatePidMode does the
            // move atomically without churning this collection.
            parent.OnPidModeChanged(Model, oldMode, value);
            OnPropertyChanged();
            // Mode flips swap which columns are live and reshape the
            // identifier display; the cells re-read these on the same tick.
            OnPropertyChanged(nameof(IsMode1A));
            OnPropertyChanged(nameof(IsMode22));
            OnPropertyChanged(nameof(IsMode2D));
            OnPropertyChanged(nameof(IsCatalogueDriven));
            OnPropertyChanged(nameof(IsHandRolled));
            OnPropertyChanged(nameof(IdentifierCatalogue));
            OnPropertyChanged(nameof(SelectedCatalogueEntry));
            OnPropertyChanged(nameof(AddressHex));
            OnPropertyChanged(nameof(IdentifierLabel));
            parent.RaisePidsChanged();
        }
    }

    public bool IsMode1A => Model.Mode == PidMode.Mode1A;
    public bool IsMode22 => Model.Mode == PidMode.Mode22;
    public bool IsMode2D => Model.Mode == PidMode.Mode2D;

    // Spec-defined name for the configured identifier, when known. Surfaced
    // as the Identifier cell tooltip so the user can hover a row to confirm
    // "$90 = VIN" without cross-referencing the spec. Returns null for
    // unknown DIDs / PIDs (no tooltip shown).
    public string? IdentifierLabel => Model.Mode switch
    {
        PidMode.Mode1A => Gmw3110DidNames.NameOf((byte)(Model.Address & 0xFF)),
        _              => null,
    };

    // $1A and $22 rows pull their entire shape (identifier, size, type,
    // scaling, unit) from a static catalogue - the user picks from a
    // dropdown rather than typing values by hand. $2D rows are the inverse:
    // every field is editable because the user is rolling a custom dynamic
    // PID from scratch (typically mirroring a memory-mapped value the real
    // ECU doesn't natively expose).
    // $22 alone uses the catalogue dropdown (a big 2-byte DID library worth picking from). $1A shows just the raw DID
    // hex - the identity DIDs are few and the user thinks in "$90", not a catalogue name - and $2D is a hand-rolled
    // 32-bit address. Both of the latter use the plain hex text box.
    public bool IsCatalogueDriven => Model.Mode == PidMode.Mode22;
    public bool IsHandRolled      => Model.Mode is PidMode.Mode1A or PidMode.Mode2D;

    // The full picker list for the current mode. Bound to the Identifier
    // cell's ComboBox.ItemsSource on $1A/$22 rows; empty (and the cell
    // collapses to a TextBox) for $2D.
    public IReadOnlyList<PidCatalogueEntry> IdentifierCatalogue
        => PidCatalogue.For(Model.Mode);

    // Round-trips the current row through the catalogue: the getter finds
    // the entry whose mode + identifier match the model (or null if the
    // row's identifier isn't in the catalogue - e.g. a config from before
    // the catalogue gained that PID). The setter stamps every shape field
    // onto the model in one shot so the read-only cells reflect the new
    // selection on the next tick.
    public PidCatalogueEntry? SelectedCatalogueEntry
    {
        get => IdentifierCatalogue.FirstOrDefault(e => e.Identifier == Model.Address);
        set
        {
            if (value is null) return;
            // Apply identifier first so AddressHex / IdentifierLabel raise
            // in the same property change burst as the rest.
            var oldAddress    = Model.Address;
            Model.Address     = value.Identifier;
            // Address is the per-mode store key, so re-key the entry; otherwise the dropdown's new identifier is
            // unreachable on the wire until the next reload.
            parent.OnPidAddressChanged(Model, oldAddress);
            Model.Name        = value.Name;
            Model.Size        = value.Size;
            Model.LengthBytes = value.LengthBytes;
            Model.DataType    = value.DataType;
            Model.Scalar      = value.Scalar;
            Model.Offset      = value.Offset;
            Model.Unit        = value.Unit;
            OnPropertyChanged(nameof(Address));
            OnPropertyChanged(nameof(AddressHex));
            OnPropertyChanged(nameof(IdentifierLabel));
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Size));
            OnPropertyChanged(nameof(DataType));
            OnPropertyChanged(nameof(Scalar));
            OnPropertyChanged(nameof(Offset));
            OnPropertyChanged(nameof(Unit));
            OnPropertyChanged(nameof(SelectedCatalogueEntry));
            RefreshLengthBytesText();
            parent.RaisePidsChanged();
        }
    }

    // Identifier display: mode-aware hex formatting.
    //   Mode1  -> "$XX"      for the 1-byte OBD-II Service $01 PID id
    //   Mode1A -> "$XX"      for the GMW3110 $1A DID byte (e.g. "$90")
    //   Mode22 -> "$XXXX"    for the GMW3110 / UDS $22 wire PID id
    //   Mode2D -> "0xXXXXXX" for the 24-bit memory address - $2D rows mirror
    //                        a memory-mapped value; the "$" prefix would be
    //                        ambiguous with the 2-byte PID prefix GM uses
    //                        for the dynamically-defined alias, so we keep
    //                        the explicit C-style "0x" here. 6 hex digits
    //                        cover the full GM ECU code/calibration address
    //                        space (typical bins are <= 2 MiB). Addresses
    //                        above $FFFFFF widen automatically.
    // The setter accepts any of those forms regardless of mode so quick
    // edits don't fight the formatter.
    public string AddressHex
    {
        get => Model.Mode switch
        {
            PidMode.Mode1A => $"${(byte)(Model.Address & 0xFF):X2}",
            PidMode.Mode2D => $"0x{Model.Address:X6}",
            _              => Model.Address <= 0xFFFF ? $"${Model.Address:X4}" : $"0x{Model.Address:X6}",
        };
        set
        {
            var trimmed = value?.Trim() ?? "";
            if (trimmed.StartsWith("$"))                                      trimmed = trimmed[1..];
            else if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
            else if (trimmed.EndsWith('h') || trimmed.EndsWith('H'))          trimmed = trimmed[..^1];
            if (uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture, out var v))
                Address = v;
        }
    }

    // $2D rows derive their wire PID id from Address as 0xF000 | (addr & 0x0FFF).
    // Two rows whose addresses share the low 12 bits collide on the wire - both
    // would respond to the same $22 request. EcuViewModel re-evaluates this
    // whenever a row's mode or address changes; the row border + tooltip in
    // the SetupWindow grid surface the warning to the user.
    public bool HasAliasCollision
    {
        get => hasAliasCollision;
        set
        {
            if (SetField(ref hasAliasCollision, value))
                OnPropertyChanged(nameof(HasWarning));
        }
    }

    public string? AliasCollisionTooltip
    {
        get => aliasCollisionTooltip;
        set => SetField(ref aliasCollisionTooltip, value);
    }

    // Combined warning indicator the row style binds to: alias collision OR
    // Nyquist-band aliasing. Either turns on the red-border row decoration.
    public bool HasWarning => hasAliasCollision || HasAliasWarning;

    public string Name
    {
        get => Model.Name;
        set { if (Model.Name != value) { Model.Name = value; OnPropertyChanged(); parent.RaisePidsChanged(); } }
    }

    public PidSize Size
    {
        get => Model.Size;
        set { if (Model.Size != value) { Model.Size = value; OnPropertyChanged(); parent.RaisePidsChanged(); } }
    }

    // Response length in bytes (1-99) as edited in the grid's Size column. Drives Pid.LengthBytes, which overrides the
    // legacy Size enum so arbitrary widths work (a VIN is 17 bytes). The raw text is held so an in-progress invalid
    // entry isn't silently reverted; the model updates only once the text validates (error shown via the row's
    // INotifyDataErrorInfo red border).
    private string lengthBytesText = "";
    public string LengthBytesText
    {
        get => lengthBytesText;
        set
        {
            if (!SetField(ref lengthBytesText, value)) return;
            var error = ValidateLengthBytes(value);
            SetError(nameof(LengthBytesText), error);
            if (error is null)
            {
                int n = int.Parse(value.Trim());
                Model.LengthBytes = n;
                // Keep the legacy Size enum coherent for the common 1/2/4 widths so other readers (catalogue picker,
                // persistence) aren't surprised; wider values fall to DWord but ResponseLength prefers LengthBytes.
                Model.Size = n switch { 1 => PidSize.Byte, 2 => PidSize.Word, _ => PidSize.DWord };
                parent.RaisePidsChanged();
            }
        }
    }

    // The Size field accepts a whole number of bytes, 1..99 (covers single sensors up to long records like VIN).
    private static string? ValidateLengthBytes(string? value)
    {
        var v = (value ?? "").Trim();
        if (!int.TryParse(v, out var n)) return "Size must be a whole number of bytes.";
        if (n < 1 || n > 99) return "Size must be between 1 and 99 bytes.";
        return null;
    }

    // Re-sync the Size text from the model when something other than the user's keystrokes changes the length (e.g.
    // the catalogue picker stamps a library entry's size). Clears any stale validation error.
    private void RefreshLengthBytesText()
    {
        lengthBytesText = Model.ResponseLength.ToString();
        SetError(nameof(LengthBytesText), null);
        OnPropertyChanged(nameof(LengthBytesText));
    }

    // The row's static response payload as human text - the value column in the editor. Identity ($1A) DIDs are
    // usually ASCII (VIN, part numbers, broadcast code), so a fully-printable payload shows as text; anything with a
    // non-printable byte shows as "0x..." hex. This is what a bin load surfaces (the extracted VIN etc.). For a $22 /
    // $2D row with no static bytes it reads blank (those rows are signal/waveform-driven; see WriteResponseBytes
    // precedence Signal > StaticBytes > waveform).
    //
    // Editing mirrors the display convention: a leading "0x" is parsed as hex bytes, otherwise the text is stored
    // verbatim as ASCII. Setting a value resizes the row to match its content length (an identity value's length IS
    // its byte count), keeping the Size column coherent.
    public string ValueText
    {
        get => BytesToDisplay(Model.StaticBytes);
        set
        {
            var bytes = ParseValue(value);
            if (bytes is null) return;                 // unparseable hex - keep the prior value rather than corrupt it
            if (BytesEqual(Model.StaticBytes, bytes)) return;

            Model.StaticBytes = bytes.Length == 0 ? null : bytes;
            if (bytes.Length != 0)
            {
                Model.LengthBytes = bytes.Length;
                // Keep the legacy Size enum coherent for 1/2/4-byte widths (other readers fall back to it).
                Model.Size = bytes.Length switch { 1 => PidSize.Byte, 2 => PidSize.Word, _ => PidSize.DWord };
                RefreshLengthBytesText();
            }
            OnPropertyChanged();
            RaiseAliasWarningChanged();   // gaining/losing static bytes flips whether the waveform (and its Nyquist risk) applies
            parent.RaisePidsChanged();
        }
    }

    // ASCII when every byte is printable; "0x"-prefixed hex otherwise. Empty for no payload.
    private static string BytesToDisplay(byte[]? b)
    {
        if (b is null || b.Length == 0) return "";
        bool printable = b.All(x => x >= 0x20 && x <= 0x7E);
        return printable ? System.Text.Encoding.ASCII.GetString(b) : "0x" + Convert.ToHexString(b);
    }

    // "0x...." -> hex bytes; anything else -> ASCII bytes. Returns null only for malformed hex (odd length / bad
    // digit), which the setter treats as "leave unchanged".
    private static byte[]? ParseValue(string? s)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0) return Array.Empty<byte>();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = t[2..].Replace(" ", "");
            if (hex.Length == 0) return Array.Empty<byte>();
            if ((hex.Length & 1) != 0) return null;
            try { return Convert.FromHexString(hex); } catch { return null; }
        }
        return System.Text.Encoding.ASCII.GetBytes(t);
    }

    private static bool BytesEqual(byte[]? a, byte[]? b)
    {
        if (a is null || a.Length == 0) return b is null || b.Length == 0;
        return b is not null && a.AsSpan().SequenceEqual(b);
    }

    public PidDataType DataType
    {
        get => Model.DataType;
        set { if (Model.DataType != value) { Model.DataType = value; OnPropertyChanged(); parent.RaisePidsChanged(); } }
    }

    public double Scalar
    {
        get => Model.Scalar;
        set { if (Model.Scalar != value) { Model.Scalar = value; OnPropertyChanged(); parent.RaisePidsChanged(); } }
    }

    public double Offset
    {
        get => Model.Offset;
        set { if (Model.Offset != value) { Model.Offset = value; OnPropertyChanged(); parent.RaisePidsChanged(); } }
    }

    public string Unit
    {
        get => Model.Unit;
        set { if (Model.Unit != value) { Model.Unit = value; OnPropertyChanged(); parent.RaisePidsChanged(); } }
    }

    // The live signal this PID reads, or null for a waveform/static PID. Typically bound on a $2D row to point a
    // custom address at a live engine signal; once set, the PID reads the engine model on the wire (encoded with the
    // row's Scalar/Offset/Size). Bound to the Signal column's picker.
    public SignalId? Signal
    {
        get => Model.Signal;
        set
        {
            if (Model.Signal == value) return;
            Model.Signal = value;
            OnPropertyChanged();
            RaiseAliasWarningChanged();   // picking/clearing a signal flips whether the waveform (and its Nyquist risk) applies
            parent.RaisePidsChanged();
        }
    }

    // Options for the Signal picker: a "(none)" entry (waveform/static) followed by every catalogue signal shown by
    // friendly name. Shared across all rows - the list never changes.
    public IReadOnlyList<SignalOption> SignalSourceOptions => SignalOptions;

    private static readonly IReadOnlyList<SignalOption> SignalOptions = BuildSignalOptions();

    private static IReadOnlyList<SignalOption> BuildSignalOptions()
    {
        var list = new List<SignalOption> { new(null, "(none)") };
        foreach (var d in SignalCatalogue.All) list.Add(new SignalOption(d.Id, d.Name));
        return list;
    }

    public string LiveValue
    {
        get => liveValue;
        set => SetField(ref liveValue, value);
    }

    public void RefreshLive(double timeMs)
    {
        // Identity DIDs ($1A) and any static-payload PID have no scalar value -
        // their "value" is the response bytes themselves (VIN / codes shown as
        // ASCII, otherwise "0x.." hex via ValueText). SampleValue returns 0 for
        // those, which is what made them read "0.00" on the dashboard. Signal-
        // and waveform-backed PIDs still report the live engineering number.
        if (Model.StaticBytes is { Length: > 0 } || Model.Mode == PidMode.Mode1A)
        {
            var text = ValueText;
            LiveValue = string.IsNullOrEmpty(text) ? "-" : text;
        }
        else
        {
            LiveValue = Model.SampleValue(timeMs).ToString("F2");
        }
    }

    // ---------------- Aliasing warning ----------------
    //
    // Flags the dramatic case: the waveform frequency is within 2 % of an
    // integer multiple of a DPID band's sample rate. Sin(2π·f·t) at sample
    // times t = 0, 1/r, 2/r, ... lands on the same phase point every sample
    // when f / r is an integer, producing a perfectly DC value on the wire.
    // Frequencies near (but not at) that ratio still produce a near-DC
    // value with very slow drift - equally surprising to a user expecting
    // to see their cycle, so worth flagging.
    //
    // The 2 % window is intentionally narrow. Frequencies between integer
    // multiples (e.g. 0.7 Hz at the Slow band, or 1.5 Hz which folds down
    // to 0.5 Hz) DO alias in the Nyquist sense, but the host sees motion -
    // a slower-than-expected cycle - rather than a frozen value. Those
    // aren't flagged because the host can usually tell something is moving;
    // it's the frozen cases that fool the user into thinking the simulator
    // or host is broken.
    //
    // DPID bands (defined in Core/Scheduler/DpidScheduler.cs):
    //   Slow   1 Hz sample rate
    //   Medium 10 Hz
    //   Fast   25 Hz
    //
    // Constant and FileStream waveforms have no inherent frequency so the warning is suppressed for them
    // regardless of the FrequencyHz value. FileStream means "stream from the loaded bin replay" - sample timing
    // is driven by the bin's row cadence, not FrequencyHz, so the Nyquist check doesn't apply. If the user
    // flips a FileStream PID to a synthetic shape the check re-engages on the next property-change.

    /// <summary>
    /// Short, comma-separated list of DPID rate bands at which the
    /// configured waveform will alias to a near-constant (e.g. "aliases
    /// Slow", "aliases Slow, Med"). Returns null when there's no aliasing
    /// risk - bind to that null state to keep the cell empty for non-warning
    /// rows.
    /// </summary>
    public string? AliasWarning
    {
        get
        {
            var bands = AliasingBands();
            return bands.Count == 0 ? null : "aliases " + string.Join(", ", bands);
        }
    }

    /// <summary>
    /// True when this PID's waveform aliases at any DPID rate band - bound
    /// to by the DataGrid's RowStyle DataTrigger to paint a red left-border
    /// stripe on the row.
    /// </summary>
    public bool HasAliasWarning => AliasingBands().Count > 0;

    /// <summary>
    /// Long-form explanation suitable for a tooltip. Describes which bands
    /// alias, what the host will see, and the two remedies (offset the
    /// frequency, or schedule on a different band). Null when no warning.
    /// </summary>
    public string? AliasWarningTooltip
    {
        get
        {
            var bands = AliasingBands();
            if (bands.Count == 0) return null;
            return $"Waveform frequency {Waveform.FrequencyHz:0.###} Hz is within " +
                   $"2 % of an integer multiple of the {string.Join(", ", bands)} " +
                   $"DPID band's sample rate (Slow=1 Hz, Med=10 Hz, Fast=25 Hz). " +
                   $"The host will sample a near-constant value rather than the " +
                   $"cycling waveform - at exact multiples the value is perfectly DC. " +
                   $"Offset the frequency away from these multiples (e.g. 0.7 Hz, " +
                   $"1.3 Hz) or schedule the PID on a band whose sample rate isn't " +
                   $"a near-divisor of the frequency.";
        }
    }

    // True only when this row's wire value actually comes from the waveform generator. A signal-backed row reads the
    // live engine model and a static row ($1A identity, bin-extracted bytes) returns fixed bytes - neither samples the
    // waveform, so the Nyquist analysis is meaningless for them. $1A is excluded outright (identity is always static).
    // Mirrors Pid.WriteResponseBytes precedence: Signal > StaticBytes > waveform.
    private bool UsesWaveform => Model.Mode != PidMode.Mode1A
                              && Model.Signal is null
                              && (Model.StaticBytes is null || Model.StaticBytes.Length == 0);

    private List<string> AliasingBands()
    {
        var bands = new List<string>();
        if (!UsesWaveform) return bands;
        if (!ShapeUsesFrequency(Waveform.Shape)) return bands;
        double f = Waveform.FrequencyHz;
        if (AliasesAtSampleRate(f, 1.0))  bands.Add("Slow");
        if (AliasesAtSampleRate(f, 10.0)) bands.Add("Med");
        if (AliasesAtSampleRate(f, 25.0)) bands.Add("Fast");
        return bands;
    }

    // True when freq is within 2 % of any integer multiple (n ≥ 1) of
    // sampleRate. Uses relative-error around the nearest multiple so the
    // window scales with the multiple - at the 1 Hz band the flagged ranges
    // are [0.98, 1.02], [1.96, 2.04], [2.94, 3.06], … rather than a fixed
    // ±0.02 Hz across all multiples.
    private static bool AliasesAtSampleRate(double freq, double sampleRate)
    {
        if (freq <= 0) return false;
        double ratio = freq / sampleRate;
        double n = Math.Round(ratio);
        if (n < 1) return false;            // freq below the first multiple - not flagged
        double error = Math.Abs(ratio - n) / n;
        return error <= 0.02;
    }

    private static bool ShapeUsesFrequency(WaveformShape shape)
        => shape != WaveformShape.Constant
        && shape != WaveformShape.FileStream
        && shape != WaveformShape.CsvFile;
}

// One entry in a PID row's Signal-source picker: the signal id (null = "(none)") and its friendly display name.
public sealed record SignalOption(SignalId? Id, string Display);
