using Common.Signals;
using Xunit;

namespace EcuSimulator.Tests.Signals;

// Covers the EngineModel: scenario boot/easing, derived-signal correlation, the O2 texture, closed-loop detection,
// and overrides (including a pinned primary propagating into its derived signals).
public sealed class EngineModelTests
{
    [Fact]
    public void Boot_AtIdle_ReadsSteadyIdlePrimaries()
    {
        var m = new EngineModel(ScenarioId.Idle);
        Assert.Equal(ScenarioId.Idle, m.ActiveScenario);
        Assert.Equal(750.0, m.Sample(SignalId.EngineRpm, 0), 3);
        Assert.Equal(0.0, m.Sample(SignalId.VehicleSpeed, 0), 3);
        Assert.Equal(0.0, m.Sample(SignalId.ThrottlePosition, 0), 3);
    }

    [Fact]
    public void ScenarioSwitch_EasesFromOldTowardNew()
    {
        var m = new EngineModel(ScenarioId.Idle);
        m.SetScenario(ScenarioId.Cruise, nowMs: 0);

        // At the switch instant RPM is still idle (750); one RPM time constant (300 ms) later it's ~63% of the way to
        // the 2000 cruise target; far out it settles near 2000 (the small ~0.7% dither keeps it off the exact value).
        Assert.Equal(750.0, m.Sample(SignalId.EngineRpm, 0), 3);
        Assert.InRange(m.Sample(SignalId.EngineRpm, 300), 1450, 1620);
        Assert.InRange(m.Sample(SignalId.EngineRpm, 10_000), 1980, 2020);
    }

    // The dynamic AccelDecelSweep scenario, sampled at known points in its 8 s climb / 2 s limiter / 5 s coast loop
    // (period = 15 s). Boot enters the sweep at t = 0, so cycle time == bus time for the first loop.
    [Fact]
    public void Sweep_ClimbsToLimiterThenCoastsBackToFloor()
    {
        var m = new EngineModel(ScenarioId.AccelDecelSweep);

        // Bottom of the loop: off-idle floor (~800 rpm), still rolling at a standstill. Exact at t=0 (dither is 0).
        Assert.Equal(800.0, m.Sample(SignalId.EngineRpm, 0), 3);
        Assert.Equal(0.0, m.Sample(SignalId.VehicleSpeed, 0), 3);

        // Mid-climb the revs are well up from the floor and below the limiter.
        Assert.InRange(m.Sample(SignalId.EngineRpm, 4000), 2500, 5500);

        // On the limiter (within the 8-10 s hold window): pinned near 7000, throttle wide open, load high.
        Assert.InRange(m.Sample(SignalId.EngineRpm, 9000), 6900, 7100);
        Assert.InRange(m.Sample(SignalId.ThrottlePosition, 9000), 85, 100);
        Assert.True(m.Sample(SignalId.EngineLoad, 9000) > 85);

        // Back at the floor at the end of the coast (t = period = 15 s), and the next loop repeats it.
        Assert.InRange(m.Sample(SignalId.EngineRpm, 15_000), 760, 840);
        Assert.InRange(m.Sample(SignalId.EngineRpm, 19_000), m.Sample(SignalId.EngineRpm, 4000) - 60,
            m.Sample(SignalId.EngineRpm, 4000) + 60);
    }

    [Fact]
    public void Sweep_RevLimiterBounces()
    {
        var m = new EngineModel(ScenarioId.AccelDecelSweep);
        // Two samples a few ms apart on the limiter land at different rpm - the fast fuel-cut stutter, not a frozen peg.
        double a = m.Sample(SignalId.EngineRpm, 8500);
        double b = m.Sample(SignalId.EngineRpm, 8540);
        Assert.True(Math.Abs(a - b) > 1.0, $"limiter should bounce: {a} vs {b}");
        Assert.InRange(a, 6900, 7100);
        Assert.InRange(b, 6900, 7100);
    }

    [Fact]
    public void Sweep_LimiterCutKnob_SetsTheBounceAmplitude()
    {
        // A bigger rev-limiter cut swings rpm further off the 7000 limit. Sample over a slice of the hold window and
        // compare the peak deviation: the 100 rpm cut must visibly out-swing the default 25 rpm one.
        static double PeakDeviation(EngineModel m)
        {
            double peak = 0;
            for (double t = 8050; t <= 9950; t += 5)
                peak = Math.Max(peak, Math.Abs(m.Sample(SignalId.EngineRpm, t) - 7000));
            return peak;
        }

        var small = new EngineModel(ScenarioId.AccelDecelSweep);
        small.Sweep = SweepProfile.Default with { LimiterBounceRpm = 25 };
        var big = new EngineModel(ScenarioId.AccelDecelSweep);
        big.Sweep = SweepProfile.Default with { LimiterBounceRpm = 100 };

        Assert.True(PeakDeviation(big) > PeakDeviation(small) + 40,
            $"100 rpm cut should out-swing 25 rpm cut: {PeakDeviation(big)} vs {PeakDeviation(small)}");
    }

    [Fact]
    public void Sweep_WrapsContinuously_AtTheBottomOfTheLoop()
    {
        var m = new EngineModel(ScenarioId.AccelDecelSweep);
        // The end of the coast and the start of the next climb meet at the floor with no jump (rpm is smoothstepped
        // to zero velocity at the turn). Sample just before and just after the 15 s wrap.
        double before = m.Sample(SignalId.EngineRpm, 14_990);
        double after = m.Sample(SignalId.EngineRpm, 15_010);
        Assert.True(Math.Abs(before - after) < 20, $"wrap should be continuous: {before} vs {after}");
    }

    [Fact]
    public void Sweep_TimingKnobsRetimeTheLoop()
    {
        var m = new EngineModel(ScenarioId.AccelDecelSweep);
        m.Sweep = SweepProfile.Default with { AccelTimeMs = 2000, LimiterHoldMs = 1000, DecelTimeMs = 2000 };

        // With a 2 s climb the engine is already on the limiter at 2.5 s (it wasn't with the default 8 s climb).
        Assert.InRange(m.Sample(SignalId.EngineRpm, 2500), 6900, 7100);
    }

    [Fact]
    public void Sweep_CrossfadesInFromThePreviousOperatingPoint()
    {
        // Start at cruise (2000 rpm), then switch into the sweep at t = 0 with the default 0.5 s cross-fade.
        var m = new EngineModel(ScenarioId.Cruise);
        m.SetScenario(ScenarioId.AccelDecelSweep, nowMs: 0);

        // At the switch instant the pull begins from the cruise value, not a snap to the ~800 rpm floor.
        Assert.Equal(2000.0, m.Sample(SignalId.EngineRpm, 0), 3);
        // Mid-fade it sits between the entry value and the low sweep value (well below cruise, above the floor).
        Assert.InRange(m.Sample(SignalId.EngineRpm, 250), 900, 1900);
        // Once the fade is over the sweep value stands alone: by 600 ms it's the pure (climbing) loop value, which
        // this early in an 8 s climb is still near the off-idle floor and nowhere near the 2000 it started from.
        Assert.InRange(m.Sample(SignalId.EngineRpm, 600), 800, 1000);
    }

    [Fact]
    public void Sweep_ZeroCrossfade_SnapsStraightToTheLoop()
    {
        var m = new EngineModel(ScenarioId.Cruise);
        m.Sweep = SweepProfile.Default with { CrossfadeMs = 0 };
        m.SetScenario(ScenarioId.AccelDecelSweep, nowMs: 0);

        // No fade: the sweep starts at its off-idle floor immediately, ignoring the 2000 rpm it came from.
        Assert.Equal(800.0, m.Sample(SignalId.EngineRpm, 0), 3);
    }

    [Fact]
    public void SteadyScenario_StillWobbles_SoValuesLookLive()
    {
        var m = new EngineModel(ScenarioId.Idle);
        // Exact at t=0 - deterministic boot and wire bytes depend on this.
        Assert.Equal(750.0, m.Sample(SignalId.EngineRpm, 0), 3);

        // A steady idle drifts slightly over time so a live monitor never shows a frozen number.
        double a = m.Sample(SignalId.EngineRpm, 1500);
        double b = m.Sample(SignalId.EngineRpm, 3000);
        Assert.True(Math.Abs(a - b) > 0.1, $"idle RPM should wobble: {a} vs {b}");
        Assert.InRange(a, 730, 770);
        Assert.InRange(b, 730, 770);

        // A pinned override stays exact even mid-stream (overrides bypass the dither).
        m.SetOverride(SignalId.EngineRpm, 1234);
        Assert.Equal(1234.0, m.Sample(SignalId.EngineRpm, 1500), 3);
    }

    [Fact]
    public void DerivedAirflow_RisesWithRpmAndLoad()
    {
        var idle = new EngineModel(ScenarioId.Idle);
        var accel = new EngineModel(ScenarioId.AccelDecelSweep);

        // Sample the sweep on the rev limiter (9 s into the loop): high rpm + high load -> airflow well above idle.
        double idleMaf = idle.Sample(SignalId.MassAirFlow, 0);
        double accelMaf = accel.Sample(SignalId.MassAirFlow, 9000);
        double idleMap = idle.Sample(SignalId.ManifoldAbsolutePressure, 0);
        double accelMap = accel.Sample(SignalId.ManifoldAbsolutePressure, 9000);

        Assert.True(accelMaf > idleMaf, $"MAF should rise: idle {idleMaf} vs accel {accelMaf}");
        Assert.True(accelMap > idleMap, $"MAP should rise: idle {idleMap} vs accel {accelMap}");
    }

    [Fact]
    public void KeyOnEngineOff_NoVacuumNoAirflow()
    {
        var m = new EngineModel(ScenarioId.KeyOnEngineOff);
        // Engine off: MAP sits at barometric (no vacuum), MAF dead, voltage drops to battery rest.
        Assert.InRange(m.Sample(SignalId.ManifoldAbsolutePressure, 0), 100, 102);
        Assert.Equal(0.0, m.Sample(SignalId.MassAirFlow, 0), 3);
        Assert.InRange(m.Sample(SignalId.ControlModuleVoltage, 0), 12.0, 13.0);
    }

    [Fact]
    public void KeyOnEngineOff_TemperaturesColdSoakToIsaSeaLevel()
    {
        var off = new EngineModel(ScenarioId.KeyOnEngineOff);
        // Cold soak: coolant/intake/oil/ambient all sit at the ISA sea-level standard (15 C), within the dither band.
        Assert.Equal(15.0, off.Sample(SignalId.CoolantTemp, 0), 1);
        Assert.Equal(15.0, off.Sample(SignalId.IntakeAirTemp, 0), 1);
        Assert.Equal(15.0, off.Sample(SignalId.AmbientAirTemp, 0), 1);
        Assert.Equal(15.0, off.Sample(SignalId.EngineOilTemp, 0), 1);

        // Running, the same signals read their warm quasi-static values, not the cold-soak standard.
        var idle = new EngineModel(ScenarioId.Idle);
        Assert.Equal(90.0, idle.Sample(SignalId.CoolantTemp, 0), 1);
        Assert.Equal(95.0, idle.Sample(SignalId.EngineOilTemp, 0), 1);
    }

    [Fact]
    public void RunTimeSinceStart_ZeroWithEngineOff_CountsUpWhenRunning()
    {
        // Boots engine-off: run time is pinned at zero however long the bus has been up.
        var m = new EngineModel(ScenarioId.KeyOnEngineOff);
        Assert.Equal(0.0, m.RunTimeSecondsSinceStart(0));
        Assert.Equal(0.0, m.RunTimeSecondsSinceStart(30_000));

        // Crank at t=30 s: run time counts from the start instant, not from bus zero.
        m.SetScenario(ScenarioId.Idle, 30_000);
        Assert.Equal(0.0, m.RunTimeSecondsSinceStart(30_000), 1);
        Assert.Equal(10.0, m.RunTimeSecondsSinceStart(40_000), 1);

        // Idle -> Cruise doesn't stop the engine, so the reference is carried, not reset.
        m.SetScenario(ScenarioId.Cruise, 45_000);
        Assert.Equal(20.0, m.RunTimeSecondsSinceStart(50_000), 1);

        // Key off: back to zero immediately.
        m.SetScenario(ScenarioId.KeyOnEngineOff, 55_000);
        Assert.Equal(0.0, m.RunTimeSecondsSinceStart(60_000));
    }

    [Fact]
    public void RunTimeSinceStart_BootsRunning_CountsFromZero()
    {
        var m = new EngineModel(ScenarioId.Idle);   // already running at boot -> running since t=0
        Assert.Equal(5.0, m.RunTimeSecondsSinceStart(5_000), 1);
    }

    [Fact]
    public void FrontO2_SwitchesInClosedLoop()
    {
        var m = new EngineModel(ScenarioId.Idle);
        // A quarter of the 1.2 Hz period apart, the switching sensor lands at clearly different voltages.
        double a = m.Sample(SignalId.O2VoltageBank1Sensor1, 0);
        double b = m.Sample(SignalId.O2VoltageBank1Sensor1, 208);
        Assert.InRange(a, 0.0, 1.275);
        Assert.InRange(b, 0.0, 1.275);
        Assert.True(Math.Abs(a - b) > 0.2, $"closed-loop O2 should switch: {a} vs {b}");
    }

    [Fact]
    public void FrontO2_PegsRichAtWideOpenThrottle()
    {
        var m = new EngineModel(ScenarioId.AccelDecelSweep);
        // On the rev limiter (9 s into the loop) the engine is at WOT: open-loop power enrichment, front O2 pegs rich.
        Assert.True(m.Sample(SignalId.O2VoltageBank1Sensor1, 9000) > 0.8);
        Assert.True(m.Sample(SignalId.O2VoltageBank1Sensor1, 9208) > 0.8);
    }

    [Theory]
    [InlineData(ScenarioId.Idle, true)]
    [InlineData(ScenarioId.Cruise, true)]
    [InlineData(ScenarioId.KeyOnEngineOff, false)]
    public void ClosedLoop_MatchesOperatingPoint(ScenarioId scenario, bool expectedClosed)
    {
        var m = new EngineModel(scenario);
        Assert.Equal(expectedClosed, m.IsClosedLoop(0));
    }

    [Fact]
    public void Sweep_LeavesClosedLoop_OnTheLimiterAndOnOverrun()
    {
        var m = new EngineModel(ScenarioId.AccelDecelSweep);
        // WOT on the limiter (9 s) is open-loop power enrichment; the coast (12 s, throttle shut, still rolling) is
        // decel fuel cutoff. Neither is closed loop. Idle floor at the bottom of the loop (t=0) still is.
        Assert.True(m.IsClosedLoop(0), "off-idle floor should be closed loop");
        Assert.False(m.IsClosedLoop(9000), "rev limiter (WOT) should not be closed loop");
        Assert.False(m.IsClosedLoop(12_000), "overrun coast should not be closed loop");
    }

    [Fact]
    public void Override_PinsSignal_AndClamps()
    {
        var m = new EngineModel(ScenarioId.Idle);
        m.SetOverride(SignalId.CoolantTemp, 120);
        Assert.Equal(120.0, m.Sample(SignalId.CoolantTemp, 0), 3);

        // An out-of-range override is clamped to the signal's max (coolant tops out at 215 C).
        m.SetOverride(SignalId.CoolantTemp, 9999);
        Assert.Equal(215.0, m.Sample(SignalId.CoolantTemp, 0), 3);

        m.ClearOverride(SignalId.CoolantTemp);
        Assert.Equal(90.0, m.Sample(SignalId.CoolantTemp, 0), 3);
    }

    [Fact]
    public void Override_OnPrimary_PropagatesToDerived()
    {
        var m = new EngineModel(ScenarioId.Idle);
        Assert.True(m.Sample(SignalId.MassAirFlow, 0) > 0);   // running at idle

        // Pinning RPM to zero reads as not-running, so airflow collapses and MAP loses its vacuum.
        m.SetOverride(SignalId.EngineRpm, 0);
        Assert.Equal(0.0, m.Sample(SignalId.MassAirFlow, 0), 3);
        Assert.InRange(m.Sample(SignalId.ManifoldAbsolutePressure, 0), 100, 102);
    }
}
