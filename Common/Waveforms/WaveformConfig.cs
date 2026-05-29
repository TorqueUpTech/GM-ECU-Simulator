namespace Common.Waveforms;

public enum WaveformShape
{
    Sin,
    Triangle,
    Square,
    Sawtooth,
    // Means "stream values from the currently loaded bin replay for this PID's channel". The Pid resolves this
    // to a ReplayWaveform via its replay-waveform factory; if no bin is loaded the Pid falls back to a constant
    // zero so the dropdown selection never crashes the engine.
    FileStream,
    // Per-PID CSV replay (column A = time in seconds, column B = value). The
    // file is loaded eagerly via CsvReplayLoader when the Pid rebuilds its
    // waveform; a missing or malformed file falls back to a constant zero so
    // editor mutations never crash the engine. Validation happens at
    // file-pick time so the user gets a clear message before persisting.
    CsvFile,
    Constant,
}

// Self-describing waveform configuration. UI binds to this; the engine
// constructs the matching IWaveformGenerator via WaveformFactory (synthetic
// shapes) or via Pid.SetReplayWaveformFactory (FileStream).
public sealed class WaveformConfig
{
    public WaveformShape Shape { get; set; } = WaveformShape.Sin;
    public double Amplitude { get; set; } = 1.0;
    public double Offset { get; set; } = 0.0;
    public double FrequencyHz { get; set; } = 1.0;
    public double PhaseDeg { get; set; } = 0.0;
    public double DutyCycle { get; set; } = 0.5;        // square wave only

    // CsvFile-only: absolute path to the user-picked CSV and how playback
    // behaves once the trace runs out. Ignored by every other shape.
    public string? CsvFilePath { get; set; }
    public CsvLoopMode CsvLoopMode { get; set; } = CsvLoopMode.HoldLast;
}
