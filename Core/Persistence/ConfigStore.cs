using System.Globalization;
using System.Text;
using Common;
using Common.Persistence;
using Common.Protocol;
using Common.Replay;
using Common.Waveforms;
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
        "GmEcuSimulator");

    /// <summary>
    /// Per-mode auto-load / auto-save path under %LocalAppData%\GmEcuSimulator\.
    /// Each persistable mode owns its own file so DPS, Flash-Tool, and ECU
    /// Simulator state stay separate worlds. DPS modes get a path too, but
    /// the App lifecycle skips reading / auto-writing it - the path exists
    /// only so manual File > Save has a target.
    /// </summary>
    public static string PathForMode(AppMode mode)
        => Path.Combine(ConfigDirectory, mode.ConfigFileName());

    /// <summary>
    /// One-time rename of the legacy <c>config.json</c> to the new ECU
    /// Simulator mode filename. Runs at startup so users upgrading across
    /// this change keep their saved ECUs without manual intervention. Skips
    /// the rename when the target already exists (preserves whichever the
    /// user has been writing to most recently).
    /// </summary>
    public static void MigrateLegacyConfigFile()
    {
        try
        {
            var legacy = Path.Combine(ConfigDirectory, "config.json");
            var target = PathForMode(AppMode.EcuSimulator);
            if (File.Exists(legacy) && !File.Exists(target))
                File.Move(legacy, target);
        }
        catch
        {
            // Migration failures are non-fatal - the user falls back to
            // defaults and re-saves. Logging would need an injected sink.
        }
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
    /// File > Save and File > Export. When <paramref name="replay"/> is
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
            cfg.Ecus.Add(new EcuDto
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
                Pids = node.Pids.Select(PidDtoFrom).ToList(),
            });
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
        };
        node.SecurityModule = SecurityModuleRegistry.Create(dto.SecurityModuleId);
        node.SecurityModule?.LoadConfig(dto.SecurityModuleConfig);
        foreach (var pidDto in dto.Pids) node.AddPid(PidFrom(pidDto));
        return node;
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
        WaveformConfig = dto.Waveform.ToWaveformConfig(),
        LengthBytes = dto.LengthBytes,
        StaticBytes = HexStringToBytes(dto.StaticBytes),
    };

    private static PidDto PidDtoFrom(Pid pid) => new()
    {
        Address = pid.Address,
        Name = pid.Name,
        Size = pid.Size,
        DataType = pid.DataType,
        Scalar = pid.Scalar,
        Offset = pid.Offset,
        Unit = pid.Unit,
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
