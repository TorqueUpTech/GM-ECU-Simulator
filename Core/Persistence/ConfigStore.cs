using Common;
using Common.Persistence;
using Common.Replay;
using Common.Signals;
using Core.Bus;
using Core.Ecu;
using Core.Replay;
using Core.Security;

namespace Core.Persistence;

// Translates between the plain-data SimulatorConfig (what's on disk)
// and the live VirtualBus / EcuNode / Pid model (what runs in memory).
// Caller is responsible for stopping anything pinned to the old model
// before swapping; ApplyTo replaces VirtualBus.Nodes wholesale.
public static class ConfigStore
{
    private static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GmEcuSimulator", "config");

    /// <summary>
    /// Per-mode auto-load / auto-save path under
    /// %LocalAppData%\GmEcuSimulator\config\. Each persistable mode owns its
    /// own file so DPS and ECU Simulator state stay separate worlds. DPS modes
    /// get a path too, but the App lifecycle skips reading / auto-writing it -
    /// the path exists only so manual File > Save has a target.
    /// </summary>
    public static string PathForMode(AppMode mode)
    {
        Directory.CreateDirectory(ConfigDirectory);
        return Path.Combine(ConfigDirectory, mode.ConfigFileName());
    }

    public static void Save(SimulatorConfig cfg, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ConfigSerializer.Serialize(cfg));
    }

    public static SimulatorConfig Load(string path)
        => ConfigSerializer.Deserialize(File.ReadAllText(path));

    /// <summary>
    /// Builds a SimulatorConfig snapshot of the current bus state - for
    /// File > Save / Save As. When <paramref name="replay"/> is
    /// supplied and has a loaded bin (or a path the user wants auto-loaded),
    /// the BinReplay section is populated from it.
    /// </summary>
    public static SimulatorConfig Snapshot(
        VirtualBus bus, string? description = null, BinReplayCoordinator? replay = null)
    {
        var cfg = new SimulatorConfig
        {
            Version = SimulatorConfig.CurrentVersion,
            Description = description,
        };
        foreach (var node in bus.Nodes)
        {
            // Primed ECUs are reconstructed at startup from PrimeArchivePath;
            // writing them to the config would persist stale static-byte dumps
            // that the next prime would overwrite anyway.
            if (node.IsPrimed) continue;
            cfg.Ecus.Add(EcuDtoFrom(node));
        }
        if (replay?.FilePath != null)
        {
            cfg.BinReplay = new BinReplayConfig
            {
                FilePath = replay.FilePath,
                LoopMode = replay.LoopMode,
                AutoLoadOnStart = replay.PersistedAutoLoadOnStart,
            };
        }
        // Persist a CaptureDirectory override only when the user has pointed
        // it somewhere other than WPF's default. The default is set on every
        // launch by App.OnStartup so a null saved value still resolves to
        // the right path next time. Pre-toggle-removal configs (which
        // carried an Enabled flag in this section) load fine - the
        // serialiser silently drops the now-unknown field.
        var defaultDir = Bus.CaptureSettings.DefaultDirectory();
        if (!string.IsNullOrEmpty(bus.Capture.CaptureDirectory)
            && !string.Equals(bus.Capture.CaptureDirectory, defaultDir,
                              StringComparison.OrdinalIgnoreCase))
        {
            cfg.BootloaderCapture = new BootloaderCaptureConfig
            {
                Directory = bus.Capture.CaptureDirectory,
            };
        }
        return cfg;
    }

    /// <summary>
    /// Replaces the bus's current ECU set with the one described by
    /// <paramref name="cfg"/>. Active periodic schedules are stopped
    /// for any ECU that is removed; remaining ECUs keep running.
    /// </summary>
    public static void ApplyTo(SimulatorConfig cfg, VirtualBus bus)
    {
        // Stop any periodic schedules tied to ECUs we're about to drop.
        foreach (var oldNode in bus.Nodes.ToArray())
            bus.Scheduler.Stop(oldNode, Array.Empty<byte>());

        bus.ReplaceNodes(cfg.Ecus.Select(EcuNodeFrom));

        // Restore any user-set CaptureDirectory override. Null means "use
        // whatever was set at startup" (WPF defaults it; tests leave it null
        // and intentionally don't write).
        if (cfg.BootloaderCapture is not null
            && !string.IsNullOrWhiteSpace(cfg.BootloaderCapture.Directory))
        {
            bus.Capture.CaptureDirectory = cfg.BootloaderCapture.Directory!;
        }
    }

    /// <summary>
    /// Builds an <see cref="EcuDto"/> snapshot of a single <see cref="EcuNode"/>.
    /// Used by the per-ECU Save command (sidebar dropdown) and by
    /// <see cref="Snapshot"/> for the whole-config save path. Primed ECUs are
    /// not skipped here - that's a policy decision the caller owns.
    /// </summary>
    public static EcuDto EcuDtoFrom(EcuNode node) => new()
    {
        Name = node.Name,
        PhysicalRequestCanId = node.PhysicalRequestCanId,
        UsdtResponseCanId = node.UsdtResponseCanId,
        UudtResponseCanId = node.UudtResponseCanId,
        Glitch = node.Glitch,
        SecurityModuleId = node.SecurityModule?.Id,
        SecurityModuleConfig = node.SecurityModuleConfig,
        FlowControlBlockSize = node.FlowControlBlockSize,
        ProgrammedState = node.ProgrammedState,
        DiagnosticAddress = node.DiagnosticAddress,
        // Persist only the off case explicitly; the default-true reload
        // path in EcuNodeFrom would round-trip true silently anyway, so
        // skipping it keeps saved configs minimal.
        AutoRespondFromLibrary = node.AutoRespondFromLibrary ? null : false,
        // Persist persona id only when it diverges from the default
        // (gmw3110). Saves a noisy "PersonaId": "gmw3110" line on every
        // ECU in the standard config and keeps diffs stable.
        PersonaId = node.Persona.Id == "gmw3110" ? null : node.Persona.Id,
        // FlashBinPath is per-persona (Ford-capture only) and EcuNode
        // doesn't carry the path back (LoadFlashBin replaces the static
        // bytes on the persona, not on the node). We DO persist the
        // node's user-set FlashBinPath via the side-channel below so a
        // round-trip through the editor doesn't drop the field. Without
        // this, the auto-save path the WPF runs would strip the field
        // and the next launch would fail Service $23 with NRC $22.
        FlashBinPath = node.FlashBinPath,
        // AllPids unions every mode-keyed store with deterministic ordering
        // (Mode22 -> Mode2D -> Mode1A -> Mode1, each by key) so saved-config
        // diffs stay stable across runs.
        Pids = node.AllPids.Select(PidDtoFrom).ToList(),
        // Persist the boot operating point only when it diverges from the default Idle (keeps standard configs quiet).
        Scenario = node.EngineModel.ActiveScenario == ScenarioId.Idle ? null : node.EngineModel.ActiveScenario,
        // Persist each AccelDecelSweep time only when the user has tuned it off the default (keeps standard configs quiet).
        SweepAccelMs = node.EngineModel.Sweep.AccelTimeMs == SweepProfile.Default.AccelTimeMs ? null : node.EngineModel.Sweep.AccelTimeMs,
        SweepLimiterHoldMs = node.EngineModel.Sweep.LimiterHoldMs == SweepProfile.Default.LimiterHoldMs ? null : node.EngineModel.Sweep.LimiterHoldMs,
        SweepDecelMs = node.EngineModel.Sweep.DecelTimeMs == SweepProfile.Default.DecelTimeMs ? null : node.EngineModel.Sweep.DecelTimeMs,
        SweepCrossfadeMs = node.EngineModel.Sweep.CrossfadeMs == SweepProfile.Default.CrossfadeMs ? null : node.EngineModel.Sweep.CrossfadeMs,
        SweepLimiterCutRpm = node.EngineModel.Sweep.LimiterBounceRpm == SweepProfile.Default.LimiterBounceRpm ? null : node.EngineModel.Sweep.LimiterBounceRpm,
        // Persist only the $01 PIDs the user has turned OFF relative to the
        // built-in E38/E67 default subset (a delta, not the whole map). null
        // when nothing is disabled keeps standard configs quiet.
        Mode1Disabled = ComputeMode1Disabled(node),
    };

    public static EcuNode EcuNodeFrom(EcuDto dto)
    {
        var node = new EcuNode
        {
            Name = dto.Name,
            PhysicalRequestCanId = dto.PhysicalRequestCanId,
            UsdtResponseCanId = dto.UsdtResponseCanId,
            UudtResponseCanId = dto.UudtResponseCanId,
            Glitch = dto.Glitch ?? Common.Glitch.GlitchConfig.CreateDefault(),
            SecurityModuleConfig = dto.SecurityModuleConfig,
            FlowControlBlockSize = dto.FlowControlBlockSize,
            ProgrammedState = dto.ProgrammedState,
            DiagnosticAddress = dto.DiagnosticAddress,
            // null in the JSON (missing field on a config saved before this
            // landed) -> true: the always-on library fallback is the new
            // default and old configs upgrade silently. Explicit false in
            // the JSON stays false so a user who turns it off per-ECU keeps
            // strict NRC behaviour across save/load.
            AutoRespondFromLibrary = dto.AutoRespondFromLibrary ?? true,
        };
        node.SecurityModule = SecurityModuleRegistry.Create(dto.SecurityModuleId);
        node.SecurityModule?.LoadConfig(dto.SecurityModuleConfig);
        // Persona resolution. Missing / unknown -> Gmw3110Persona (the
        // standard default for every GM ECU). The Ford-capture preset uses
        // PersonaId = "ford-capture" to swap in the logging dispatcher.
        node.Persona = Core.Ecu.Personas.PersonaRegistry.Resolve(dto.PersonaId);
        // Ford-capture only: load the flash bin if a path was supplied.
        // Other personas ignore FlashBinPath. We throw on missing / unreadable
        // so config-load failures are loud - $23 silently NRC-ing against a
        // typo'd path would be confusing on a re-test.
        node.FlashBinPath = dto.FlashBinPath;
        if (dto.PersonaId == "ford-capture" && !string.IsNullOrWhiteSpace(dto.FlashBinPath))
        {
            Core.Ecu.Personas.FordCapturePersona.LoadFlashBin(dto.FlashBinPath!);
        }
        foreach (var pidDto in dto.Pids)
        {
            // AddPid routes by pid.Mode into the appropriate per-mode store.
            node.AddPid(PidFrom(pidDto));
        }
        // Restore any tuned AccelDecelSweep timing before the scenario is set; each absent field falls back to the
        // SweepProfile default so a partially-specified config still loads coherently.
        if (dto.SweepAccelMs is { } || dto.SweepLimiterHoldMs is { } || dto.SweepDecelMs is { } || dto.SweepCrossfadeMs is { }
            || dto.SweepLimiterCutRpm is { })
        {
            node.EngineModel.Sweep = SweepProfile.Default with
            {
                AccelTimeMs = dto.SweepAccelMs ?? SweepProfile.Default.AccelTimeMs,
                LimiterHoldMs = dto.SweepLimiterHoldMs ?? SweepProfile.Default.LimiterHoldMs,
                DecelTimeMs = dto.SweepDecelMs ?? SweepProfile.Default.DecelTimeMs,
                CrossfadeMs = dto.SweepCrossfadeMs ?? SweepProfile.Default.CrossfadeMs,
                LimiterBounceRpm = dto.SweepLimiterCutRpm ?? SweepProfile.Default.LimiterBounceRpm,
            };
        }
        // Restore the boot operating point for the live signal model (absent -> the engine model's default Idle).
        if (dto.Scenario is { } scenario) node.EngineModel.SetScenario(scenario, 0);
        // Re-apply the saved $01 supported-PID delta: start from the built-in
        // default subset and remove the PIDs the user turned off. Absent /
        // empty -> the full default subset stands.
        if (dto.Mode1Disabled is { Count: > 0 } disabled)
            node.Mode1Supported = new HashSet<byte>(J1979Catalogue.DefaultSupported.Except(disabled));
        return node;
    }

    // The $01 supported set is stored as a delta off the built-in default
    // subset: which default PIDs has the user disabled? Returns null when the
    // node still advertises the full default set (the common case).
    private static List<byte>? ComputeMode1Disabled(EcuNode node)
    {
        var disabled = J1979Catalogue.DefaultSupported
            .Where(p => !node.Mode1Supported.Contains(p))
            .OrderBy(p => p)
            .ToList();
        return disabled.Count == 0 ? null : disabled;
    }

    public static Pid PidFrom(PidDto dto) => new()
    {
        Address = dto.Address,
        Name = dto.Name,
        Size = dto.Size,
        DataType = dto.DataType,
        Scalar = dto.Scalar,
        Offset = dto.Offset,
        Unit = dto.Unit,
        Mode = dto.Mode,
        Signal = dto.Signal,
        WaveformConfig = dto.Waveform.ToWaveformConfig(),
        LengthBytes = dto.LengthBytes,
        StaticBytes = HexStringToBytes(dto.StaticBytes),
    };

    public static PidDto PidDtoFrom(Pid pid) => new()
    {
        Address = pid.Address,
        Name = pid.Name,
        Size = pid.Size,
        DataType = pid.DataType,
        Scalar = pid.Scalar,
        Offset = pid.Offset,
        Unit = pid.Unit,
        Mode = pid.Mode,
        Signal = pid.Signal,
        Waveform = WaveformDto.From(pid.WaveformConfig),
        LengthBytes = pid.LengthBytes,
        StaticBytes = BytesToHexString(pid.StaticBytes),
    };

    private static byte[]? HexStringToBytes(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        var s = hex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if ((s.Length & 1) != 0)
            throw new FormatException($"PID staticBytes hex length must be even, got {s.Length}: {hex}");
        var bytes = new byte[s.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(s.AsSpan(i * 2, 2),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
        return bytes;
    }

    private static string? BytesToHexString(byte[]? bytes)
    {
        if (bytes is null) return null;
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
