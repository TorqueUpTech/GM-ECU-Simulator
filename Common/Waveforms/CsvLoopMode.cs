namespace Common.Waveforms;

// What CsvWaveform.Sample() returns once playback runs past the last row of
// the CSV. Mirrors BinReplayLoopMode in Common.Replay but stays in
// Common.Waveforms to keep the per-PID CSV path self-contained.
public enum CsvLoopMode
{
    HoldLast,
    Loop,
    Stop,
}
