namespace Common.Waveforms;

// Constructs the IWaveformGenerator for a synthetic-shape WaveformConfig. FileStream is intentionally NOT handled here
// - it represents bin-replay data which only Pid can resolve (it owns the replay-waveform factory). Pid routes around
// the factory when Shape == FileStream and only forwards the synthetic shapes here, so the FileStream branch in the
// switch would be unreachable; we throw to surface a misuse instead.
//
// CsvFile is handled here: the path is read eagerly via CsvReplayLoader so a bad / missing file degrades to a
// ConstantWaveform(0) rather than throwing out of a property setter. The picker UI validates up-front so the user
// sees the failure reason before the path is persisted; this fallback only matters when a saved config references a
// file that has since been moved or deleted.
public static class WaveformFactory
{
    public static IWaveformGenerator Create(WaveformConfig cfg) => cfg.Shape switch
    {
        WaveformShape.Sin        => new SinWaveform(cfg.Amplitude, cfg.Offset, cfg.FrequencyHz, cfg.PhaseDeg),
        WaveformShape.Triangle   => new TriangleWaveform(cfg.Amplitude, cfg.Offset, cfg.FrequencyHz, cfg.PhaseDeg),
        WaveformShape.Square     => new SquareWaveform(cfg.Amplitude, cfg.Offset, cfg.FrequencyHz, cfg.PhaseDeg, cfg.DutyCycle),
        WaveformShape.Sawtooth   => new SawtoothWaveform(cfg.Amplitude, cfg.Offset, cfg.FrequencyHz, cfg.PhaseDeg),
        WaveformShape.Constant   => new ConstantWaveform(cfg.Offset),
        WaveformShape.CsvFile    => BuildCsv(cfg),
        WaveformShape.FileStream => throw new InvalidOperationException("FileStream is bin-replay data; Pid resolves it via SetReplayWaveformFactory, not WaveformFactory."),
                               _ => throw new ArgumentOutOfRangeException(nameof(cfg.Shape)),
    };

    private static IWaveformGenerator BuildCsv(WaveformConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.CsvFilePath) || !File.Exists(cfg.CsvFilePath))
            return new ConstantWaveform(0);
        try
        {
            var loaded = CsvReplayLoader.Load(cfg.CsvFilePath);
            return new CsvWaveform(loaded.Samples, cfg.CsvLoopMode);
        }
        catch
        {
            return new ConstantWaveform(0);
        }
    }
}
