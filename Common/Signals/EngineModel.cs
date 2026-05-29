using System.Collections.Concurrent;

namespace Common.Signals;

// The live engine model for one ECU: the runtime half of the signal layer. It holds the active scenario and turns it
// into a continuously-readable value for any signal at a given time. Reads are stateless and time-driven - you pass
// the bus clock (ms) to Sample and get the value at that instant - so the same model serves both on-demand reads
// (the $01/$22 handlers) and the periodic $AA scheduler without needing a tick loop of its own.
//
// Behaviour by signal group:
//   - Primaries (rpm/speed/throttle/load) ease from their old value toward the new scenario's target on a per-signal
//     time constant, so a scenario switch ramps instead of teleporting.
//   - Derived signals (MAP, MAF, timing, trims, O2, ...) are recomputed from the current primaries on every read,
//     which is what keeps them mutually consistent for free.
//   - A handful are quasi-static warm-engine constants until the thermal axis is added.
//
// Overrides pin a signal to a fixed value; pinning a primary propagates through the derivations (pin load and MAP/MAF
// follow), which is the "pin the underlying quantity and every mode reflects it" behaviour the design calls for.
//
// Thread-safety: the easing state is swapped as one immutable snapshot behind a volatile reference, so a Sample on
// the scheduler thread never sees a half-applied scenario change; overrides live in a concurrent map.
public sealed class EngineModel
{
    private readonly record struct PrimaryEase(double Start, double Target, double StartTimeMs);

    private sealed class EaseState
    {
        public required ScenarioId Scenario { get; init; }
        public required IReadOnlyDictionary<SignalId, PrimaryEase> Primaries { get; init; }
        // Bus time the scenario was entered. AccelDecelSweep measures its cycle phase from here so the rev pull
        // always starts at the bottom of the loop the moment the user selects it.
        public required double StartTimeMs { get; init; }
        // Bus time the engine last cranked, or null while it is not running. Set on the not-running -> running edge and
        // carried across running -> running switches (Idle<->Cruise<->Sweep don't stop the engine); drives the $1F
        // run-time-since-start PID. Null whenever the active scenario is engine-off (KOEO), so that PID reads zero.
        public required double? EngineStartMs { get; init; }
    }

    // The four scenario-driven signals, and how fast each chases its target. RPM and throttle snap in well under a
    // second; road speed lags behind (vehicle inertia); load tracks airflow demand a touch slower than the throttle.
    private static readonly SignalId[] PrimarySignals =
    {
        SignalId.EngineRpm, SignalId.VehicleSpeed, SignalId.ThrottlePosition, SignalId.EngineLoad,
    };

    private static readonly IReadOnlyDictionary<SignalId, double> TauMs = new Dictionary<SignalId, double>
    {
        [SignalId.EngineRpm] = 300,
        [SignalId.VehicleSpeed] = 4000,
        [SignalId.ThrottlePosition] = 200,
        [SignalId.EngineLoad] = 400,
    };

    private volatile EaseState ease;
    private readonly ConcurrentDictionary<SignalId, double> overrides = new();

    // Tunable shape of the AccelDecelSweep rev pull. A reference swap keeps reads lock-free; tweaking the knobs mid-
    // sweep just changes the timing the next Sample sees (it can shift the phase, which is fine for a config knob).
    private volatile SweepProfile sweep = SweepProfile.Default;
    public SweepProfile Sweep { get => sweep; set => sweep = value; }

    public EngineModel(ScenarioId initial = ScenarioId.Idle)
    {
        // Boot already settled at the initial scenario (start == target) so a fresh sim reads steady values from t=0.
        // A model that boots running has been running since t=0; one that boots engine-off has no start reference.
        ease = BuildEase(initial, current: null, startTimeMs: 0, engineStartMs: ScenarioRunning(initial) ? 0 : null);
    }

    public ScenarioId ActiveScenario => ease.Scenario;

    // Whether a scenario's steady operating point has the engine turning. Used as the engine-on/off discriminator for
    // the run-time reference; KOEO is the only non-running point (zero target rpm).
    private static bool ScenarioRunning(ScenarioId id) => ScenarioCatalogue.Get(id).EngineRpm > 50;

    // Switch operating point. Captures each primary's current eased value as the new start so the ramp to the new
    // target stays continuous even when a switch lands mid-transition.
    public void SetScenario(ScenarioId id, double nowMs)
    {
        var e = ease;
        var current = new Dictionary<SignalId, double>(PrimarySignals.Length);
        // Capture each primary's live value (eased, swept, or pinned) as the new ramp start, so leaving a scenario -
        // including mid-sweep - eases continuously from wherever the signal actually is right now.
        foreach (var sig in PrimarySignals)
            current[sig] = Primary(sig, e, nowMs);

        // Maintain the engine-start reference across the switch: off -> running cranks now, running -> running keeps the
        // existing reference (the engine didn't stop), and any switch into a non-running point clears it.
        double? engineStart = !ScenarioRunning(id) ? null
                            : ScenarioRunning(e.Scenario) ? e.EngineStartMs ?? nowMs
                            : nowMs;
        ease = BuildEase(id, current, nowMs, engineStart);
    }

    // Run time since the engine last started, in seconds. Zero whenever the engine is not running (KOEO); otherwise the
    // bus time elapsed since the not-running -> running transition. This is what OBD-II PID $1F reports.
    public double RunTimeSecondsSinceStart(double t)
    {
        double? start = ease.EngineStartMs;
        if (start is null) return 0;
        double secs = (t - start.Value) / 1000.0;
        return secs < 0 ? 0 : secs;
    }

    // Pin a signal to a fixed engineering value (e.g. force coolant to 120 C). Pinning a primary flows through to its
    // derived signals; pinning a derived signal fixes only that one. Clamped to the signal's range on read.
    public void SetOverride(SignalId id, double value) => overrides[id] = value;

    public void ClearOverride(SignalId id) => overrides.TryRemove(id, out _);

    public bool HasOverride(SignalId id) => overrides.ContainsKey(id);

    // The current engineering value of a signal at bus time t. The single entry point every live projection uses.
    public double Sample(SignalId id, double timeMs)
    {
        if (overrides.TryGetValue(id, out var ov)) return Clamp(id, ov);   // a pinned value is exact - never dithered
        var e = ease;
        double v = IsPrimary(id) ? Primary(id, e, timeMs) : ComputeDerived(id, e, timeMs);
        return Clamp(id, v + Dither(v, timeMs, (int)id));
    }

    // Whether the engine is in closed-loop fuelling right now. Shared by the O2/trim derivations and (later) the $03
    // fuel-system-status discrete state so both agree on one definition of closed vs open loop.
    public bool IsClosedLoop(double timeMs)
    {
        var e = ease;
        var (_, _, _, closed) = Flags(
            Primary(SignalId.EngineRpm, e, timeMs),
            Primary(SignalId.EngineLoad, e, timeMs),
            Primary(SignalId.ThrottlePosition, e, timeMs),
            Primary(SignalId.VehicleSpeed, e, timeMs));
        return closed;
    }

    private static bool IsPrimary(SignalId id) =>
        id is SignalId.EngineRpm or SignalId.VehicleSpeed or SignalId.ThrottlePosition or SignalId.EngineLoad;

    // A primary's eased value, honouring an override so a pinned primary feeds the derivations its pinned value.
    private double Primary(SignalId id, EaseState e, double t)
    {
        if (overrides.TryGetValue(id, out var ov)) return ov;                  // a pinned primary wins, even mid-sweep
        if (e.Scenario == ScenarioId.AccelDecelSweep) return SweepPrimary(id, e, t);
        return EaseValue(e.Primaries[id], t, TauMs[id]);
    }

    // The AccelDecelSweep primaries: a looping rev pull built from three back-to-back legs. The whole point is that
    // rpm and road speed are smooth (smoothstepped to zero velocity at each turn, so the bottom-of-coast -> top-of-
    // climb wrap is seamless), while throttle and load switch character per leg - which is the driver flooring it,
    // holding it against the limiter, then lifting off. Everything derived (MAP, MAF, O2, trims, DFCO, closed-loop)
    // follows for free because it all reads these four primaries through Primary().
    //   1. Climb   [0, AccelTimeMs):           off idle up to the rev limiter, throttle opening, load building.
    //   2. Limiter [AccelTimeMs, +LimiterHoldMs): foot flat at the limiter, rpm bouncing on the fuel/spark cut.
    //   3. Coast   [.., PeriodMs):              lifted off, rpm and speed coasting back down to a standstill.
    private double SweepPrimary(SignalId id, EaseState e, double t)
    {
        var sp = sweep;
        double swept = SweepLoopValue(id, e, sp, t);

        // Entry cross-fade: blend from the value captured when the sweep was entered (e.Primaries[id].Start) toward
        // the live loop value over the first CrossfadeMs, so switching in from idle/cruise eases into the pull rather
        // than snapping to the off-idle floor. Smoothstepped so both ends of the fade are velocity-continuous. After
        // the window (or on a boot/zero-fade) the loop value stands alone.
        double dt = t - e.StartTimeMs;
        if (sp.CrossfadeMs > 0 && dt >= 0 && dt < sp.CrossfadeMs)
        {
            double entry = e.Primaries[id].Start;
            return Lerp(entry, swept, SmoothStep(dt / sp.CrossfadeMs));
        }
        return swept;
    }

    // The pure, looping sweep value for a primary - no entry cross-fade. Period-wrapped so it repeats forever.
    private static double SweepLoopValue(SignalId id, EaseState e, SweepProfile sp, double t)
    {
        double period = sp.PeriodMs;
        if (period <= 0) return SweepFloor(id, sp);            // degenerate all-zero config: sit at the off-idle floor

        double u = (t - e.StartTimeMs) % period;
        if (u < 0) u += period;                                // a read before the sweep started -> bottom of the loop
        double accelEnd = sp.AccelTimeMs;
        double limiterEnd = sp.AccelTimeMs + sp.LimiterHoldMs;

        if (u < accelEnd)                                      // leg 1: climbing off idle to the limiter
        {
            double s = SmoothStep(u / sp.AccelTimeMs);
            return id switch
            {
                SignalId.EngineRpm => Lerp(sp.RpmLow, sp.RpmHigh, s),
                SignalId.VehicleSpeed => Lerp(sp.SpeedLow, sp.SpeedHigh, s),
                SignalId.ThrottlePosition => Lerp(sp.ThrottleIdle, sp.ThrottleOpen, s),
                SignalId.EngineLoad => Lerp(sp.LoadIdle, sp.LoadHigh, s),
                _ => 0,
            };
        }
        if (u < limiterEnd)                                    // leg 2: bouncing off the rev limiter, foot flat
        {
            // The bounce is a sine starting at zero (tHold = 0 -> continuous with the top of the climb) so the
            // limiter hit doesn't jump; it's fast and small relative to the band, reading as a fuel-cut stutter.
            double tHold = (u - accelEnd) / 1000.0;
            double bounce = sp.LimiterBounceRpm * Math.Sin(2 * Math.PI * sp.LimiterBounceHz * tHold);
            return id switch
            {
                SignalId.EngineRpm => sp.RpmHigh + bounce,
                SignalId.VehicleSpeed => sp.SpeedHigh,
                SignalId.ThrottlePosition => sp.ThrottleOpen,
                SignalId.EngineLoad => sp.LoadHigh,
                _ => 0,
            };
        }
        // leg 3: lifted off - rpm and speed coast back down to the floor; throttle shut + still rolling reads as DFCO.
        double d = SmoothStep((u - limiterEnd) / sp.DecelTimeMs);
        return id switch
        {
            SignalId.EngineRpm => Lerp(sp.RpmHigh, sp.RpmLow, d),
            SignalId.VehicleSpeed => Lerp(sp.SpeedHigh, sp.SpeedLow, d),
            SignalId.ThrottlePosition => 0,
            SignalId.EngineLoad => sp.LoadOverrun,
            _ => 0,
        };
    }

    // The off-idle floor of the sweep - where every loop begins and ends. Only used for a degenerate zero-length
    // profile; the live sweep never sits here.
    private static double SweepFloor(SignalId id, SweepProfile sp) => id switch
    {
        SignalId.EngineRpm => sp.RpmLow,
        SignalId.VehicleSpeed => sp.SpeedLow,
        SignalId.ThrottlePosition => sp.ThrottleIdle,
        SignalId.EngineLoad => sp.LoadIdle,
        _ => 0,
    };

    // Hermite smoothstep: 0 and 1 at the ends with zero slope at both, so a leg eases in and out. The zero slope at
    // the turning points is what makes the loop wrap without a velocity kink. Clamped so out-of-range phase is safe.
    private static double SmoothStep(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        return x * x * (3 - 2 * x);
    }

    private static double Lerp(double a, double b, double f) => a + (b - a) * f;

    // First-order approach toward target: value = target + (start - target) * e^(-dt/tau). At the switch instant this
    // equals start; given enough time it settles at target.
    private static double EaseValue(in PrimaryEase p, double t, double tauMs)
    {
        double dt = t - p.StartTimeMs;
        if (dt <= 0 || tauMs <= 0) return dt <= 0 ? p.Start : p.Target;
        return p.Target + (p.Start - p.Target) * Math.Exp(-dt / tauMs);
    }

    private static EaseState BuildEase(ScenarioId id, IReadOnlyDictionary<SignalId, double>? current, double startTimeMs,
        double? engineStartMs)
    {
        var s = ScenarioCatalogue.Get(id);
        var prim = new Dictionary<SignalId, PrimaryEase>(PrimarySignals.Length);
        foreach (var sig in PrimarySignals)
        {
            double target = PrimaryTarget(s, sig);
            double start = current != null && current.TryGetValue(sig, out var v) ? v : target;
            prim[sig] = new PrimaryEase(start, target, startTimeMs);
        }
        return new EaseState { Scenario = id, Primaries = prim, StartTimeMs = startTimeMs, EngineStartMs = engineStartMs };
    }

    private static double PrimaryTarget(Scenario s, SignalId id) => id switch
    {
        SignalId.EngineRpm => s.EngineRpm,
        SignalId.VehicleSpeed => s.VehicleSpeed,
        SignalId.ThrottlePosition => s.ThrottlePosition,
        SignalId.EngineLoad => s.EngineLoad,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "not a primary signal"),
    };

    // Operating-condition flags derived from the four primaries. WOT trips open-loop power enrichment; a shut throttle
    // while still rolling is decel fuel cutoff (DFCO); closed loop is the normal running case that is neither.
    private static (bool running, bool wot, bool overrunCut, bool closedLoop) Flags(
        double rpm, double load, double throttle, double speed)
    {
        bool running = rpm > 50;
        bool wot = load > 85;
        bool overrunCut = running && throttle < 1 && speed > 3;
        bool closedLoop = running && !wot && !overrunCut;
        return (running, wot, overrunCut, closedLoop);
    }

    // Everything that isn't a scenario primary: physically-plausible functions of the current primaries plus the warm
    // quasi-static constants. Deliberately simple (algebraic, not time-integrating) - faithful enough that related
    // PIDs move together, with real tuning left for later.
    private double ComputeDerived(SignalId id, EaseState e, double t)
    {
        double rpm = Primary(SignalId.EngineRpm, e, t);
        double load = Primary(SignalId.EngineLoad, e, t);
        double throttle = Primary(SignalId.ThrottlePosition, e, t);
        double speed = Primary(SignalId.VehicleSpeed, e, t);
        var (running, wot, overrunCut, closed) = Flags(rpm, load, throttle, speed);

        const double baro = 101.0;
        double ts = t / 1000.0;
        double map = running ? baro * (0.18 + 0.0082 * load) : baro;

        // Key-on-engine-off is a cold soak: coolant/intake/oil have equalised to ambient, and ambient itself sits at the
        // ISA sea-level standard (15 C). While running they read their warm quasi-static values instead.
        const double IsaSeaLevelTempC = 15.0;
        bool engineOff = e.Scenario == ScenarioId.KeyOnEngineOff;

        return id switch
        {
            // Temperatures: warm quasi-static when running, cold-soaked to the ISA sea-level standard with the engine off.
            SignalId.CoolantTemp => engineOff ? IsaSeaLevelTempC : 90,
            SignalId.IntakeAirTemp => engineOff ? IsaSeaLevelTempC : 35,
            SignalId.AmbientAirTemp => engineOff ? IsaSeaLevelTempC : 25,
            SignalId.FuelLevel => 57,
            SignalId.EngineOilTemp => engineOff ? IsaSeaLevelTempC : 95,
            SignalId.BarometricPressure => baro,

            // Charge / airflow. No vacuum when the engine is off (MAP sits at barometric), MAF dead.
            SignalId.ManifoldAbsolutePressure => map,
            SignalId.MassAirFlow => running ? rpm * map * 1.5e-4 : 0,

            // Spark and electrical. More advance at light load; charging voltage only while running.
            SignalId.TimingAdvance => running ? 32.0 - 0.2 * load : 0,
            SignalId.ControlModuleVoltage => running ? 14.2 : 12.6,

            // Pedal echoes the commanded throttle (two redundant channels).
            SignalId.AcceleratorPedalD => throttle,
            SignalId.AcceleratorPedalE => throttle,

            // Fuelling. Closed loop dithers around stoich; WOT commands rich.
            SignalId.CommandedEquivalenceRatio => wot ? 0.85 : 1.0,
            SignalId.ShortTermFuelTrimBank1 => closed ? 4.0 * Math.Sin(2 * Math.PI * 0.4 * ts) : 0,
            SignalId.ShortTermFuelTrimBank2 => closed ? 4.0 * Math.Sin(2 * Math.PI * 0.4 * ts + 0.7) : 0,
            SignalId.LongTermFuelTrimBank1 => 2.0,
            SignalId.LongTermFuelTrimBank2 => 1.5,

            // O2 sensors. Front sensors switch in closed loop (the texture), peg rich at WOT and lean on fuel cut;
            // rear (post-cat) sensors sit high and steady. Inactive when the engine is off.
            SignalId.O2VoltageBank1Sensor1 => FrontO2(running, wot, overrunCut, ts, 0.0),
            SignalId.O2VoltageBank2Sensor1 => FrontO2(running, wot, overrunCut, ts, 1.1),
            SignalId.O2VoltageBank1Sensor2 => running ? 0.65 : 0.45,
            SignalId.O2VoltageBank2Sensor2 => running ? 0.65 : 0.45,

            _ => 0,
        };
    }

    // Front (pre-cat) O2 voltage: rich/lean pegs in open loop, otherwise the 0.1-0.9 V switching that a healthy
    // closed-loop sensor shows. The phase argument decorrelates the two banks so they don't switch in lockstep.
    private static double FrontO2(bool running, bool wot, bool overrunCut, double ts, double phase)
    {
        if (!running) return 0.45;
        if (wot) return 0.85;
        if (overrunCut) return 0.10;
        return 0.45 + 0.35 * Math.Sin(2 * Math.PI * 1.2 * ts + phase);
    }

    // A small, smooth, value-proportional wobble layered on every sampled value so a steady operating point still
    // reads "alive" (idle hunt, sensor noise) instead of a frozen number. Proportional to the current value, so a
    // signal at rest (rpm/speed/airflow = 0 with the engine off) stays exactly at rest. The seed detunes the two sines
    // per signal so they wobble independently; because the seed shifts frequency rather than phase, every signal reads
    // its exact target at t = 0, which keeps boot values and pinned-override reads deterministic.
    private static double Dither(double value, double timeMs, int seed)
    {
        double t = timeMs / 1000.0;
        double n = 0.6 * Math.Sin(2 * Math.PI * (0.37 + 0.017 * seed) * t)
                 + 0.4 * Math.Sin(2 * Math.PI * (0.91 + 0.023 * seed) * t);
        return value * 0.007 * n;
    }

    private static double Clamp(SignalId id, double v)
    {
        var d = SignalCatalogue.Get(id);
        return v < d.Min ? d.Min : v > d.Max ? d.Max : v;
    }
}
