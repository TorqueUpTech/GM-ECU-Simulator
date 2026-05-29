namespace Common.Waveforms;

// Streams a pre-parsed CSV trace as the PID's wire value. Time origin is
// lazy: the first Sample() call captures wall-clock time and treats the
// first CSV row as t = 0. Subsequent samples step-hold to the latest row
// whose CSV timestamp is <= elapsed seconds.
//
// Past the end of the trace, behaviour is governed by CsvLoopMode:
//   HoldLast - keep returning the final row's value (default).
//   Loop     - wrap elapsed time modulo the trace duration.
//   Stop     - return zero.
public sealed class CsvWaveform : IWaveformGenerator
{
    private readonly double[] times;
    private readonly double[] values;
    private readonly double durationSec;
    private readonly CsvLoopMode loopMode;

    private readonly object startLock = new();
    private volatile bool started;
    private double startMs;

    public CsvWaveform(IReadOnlyList<(double TimeSec, double Value)> samples, CsvLoopMode loopMode)
    {
        if (samples == null || samples.Count == 0)
            throw new ArgumentException("CSV must contain at least one sample.", nameof(samples));

        times = new double[samples.Count];
        values = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            times[i] = samples[i].TimeSec;
            values[i] = samples[i].Value;
        }
        durationSec = times[^1] - times[0];
        this.loopMode = loopMode;
    }

    public double Sample(double timeMs)
    {
        if (!started)
        {
            lock (startLock)
            {
                if (!started) { startMs = timeMs; started = true; }
            }
        }

        double elapsedSec = (timeMs - startMs) / 1000.0;

        if (durationSec <= 0) return values[0];

        if (elapsedSec >= durationSec)
        {
            switch (loopMode)
            {
                case CsvLoopMode.HoldLast: return values[^1];
                case CsvLoopMode.Stop:     return 0.0;
                case CsvLoopMode.Loop:     elapsedSec %= durationSec; break;
            }
        }
        else if (elapsedSec < 0) return values[0];

        double t = times[0] + elapsedSec;
        int lo = 0, hi = times.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (times[mid] <= t) lo = mid; else hi = mid - 1;
        }
        return values[lo];
    }
}
