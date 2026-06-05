using Common.Dbc;
using Common.Protocol;
using Common.Signals;
using Core.Ecu;

namespace Core.Dbc;

// Turns a parsed DBC into the simulator's runtime broadcast model, scoped to one transmitter and an
// explicit message pick (a DBC describes the whole bus; an ECU broadcasts only its own module's
// messages). Also auto-maps signals to live engine signals by name/unit heuristic and merges a
// re-import into an existing set.
//
// Lives in Core (not Common) because it produces Core.Ecu.BroadcastMessage; the parser + codec it
// builds on stay in Common.Dbc.
public static class DbcImporter
{
    // Default transmit period when the DBC carries no GenMsgCycleTime for a message.
    public const int DefaultPeriodMs = 100;

    // Transmitter nodes that send at least one message, most-prolific first - the import picker's
    // dropdown order (ECM_HS sends the bulk on a GM HS bus, so it floats to the top).
    public static IReadOnlyList<(string Transmitter, int Count)> TransmittersByMessageCount(DbcDatabase db)
        => db.Messages
            .GroupBy(m => m.Transmitter)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(t => t.Item2)
            .ThenBy(t => t.Key, StringComparer.Ordinal)
            .ToList();

    // Convert the chosen messages (by raw CAN id) of one transmitter into broadcast messages, each
    // signal seeded with its auto-mapped live source (unmapped -> None/0, editable afterward).
    public static List<BroadcastMessage> ToBroadcasts(DbcDatabase db, string transmitter, IReadOnlySet<uint> selectedIds)
        => db.Messages
            .Where(m => m.Transmitter == transmitter && selectedIds.Contains(m.Id))
            .Select(ToBroadcast)
            .ToList();

    public static BroadcastMessage ToBroadcast(DbcMessage m)
    {
        var msg = new BroadcastMessage
        {
            CanId = m.Id,
            Extended = m.Extended,
            Name = m.Name,
            Dlc = m.Dlc,
            PeriodMs = m.CycleTimeMs is > 0 ? m.CycleTimeMs!.Value : DefaultPeriodMs,
            Enabled = true,
        };
        foreach (var s in m.Signals)
        {
            var mapped = AutoMap(s);
            msg.Signals.Add(new BroadcastSignal
            {
                Name = s.Name,
                StartBit = s.StartBit,
                Length = s.Length,
                ByteOrder = s.ByteOrder,
                Signed = s.Signed,
                Scale = s.Scale,
                Offset = s.Offset,
                Unit = s.Unit,
                Min = s.Min,
                Max = s.Max,
                Signal = mapped,
                ValueSource = mapped.HasValue ? BroadcastValueSource.Signal : BroadcastValueSource.None,
            });
        }
        return msg;
    }

    // True when the message carries at least one signal we can drive from the live engine model -
    // the picker uses this to pre-tick "interesting" messages.
    public static bool HasMappableSignal(DbcMessage m) => m.Signals.Any(s => AutoMap(s) is not null);

    // Fold a scoped re-import into an ECU that already has broadcasts, returning the new set (sorted
    // by CAN id). Used when the import picker ran in reconcile mode:
    //   * existing   - the ECU's current broadcast rows.
    //   * matchedIds - existing ids the DBC re-defines with an IDENTICAL shape (same id + same DLC/
    //                  signal layout) - the pre-ticked "true re-import" rows. Removals are scoped to
    //                  THIS set: only a matched row that was de-ticked is dropped. A row whose id the
    //                  DBC doesn't define, or defines with a different shape (a collision), is never
    //                  removed here - it is left alone unless explicitly replaced (see replaceIds).
    //   * selectedIds  - the ids left ticked on OK.
    //   * incoming     - freshly converted broadcast messages for the ticked ids.
    //   * replaceIds   - ticked ids the user chose to overwrite with the imported definition (the
    //                    CAN-id-collision case: this DBC reuses an existing id for a differently
    //                    shaped message). For these the existing row is dropped and the incoming one
    //                    used; their prior mappings are intentionally discarded (they don't apply to
    //                    a different layout). Default empty -> the pure same-shape re-import path.
    // A matched row that was de-ticked is dropped; a matched row still ticked is kept as-is (its user
    // mappings preserved); a ticked id new to the table is appended; a collision id is kept unless it
    // is in replaceIds.
    public static List<BroadcastMessage> Reconcile(
        IReadOnlyList<BroadcastMessage> existing,
        IReadOnlySet<uint> matchedIds,
        IReadOnlySet<uint> selectedIds,
        IReadOnlyList<BroadcastMessage> incoming,
        IReadOnlySet<uint>? replaceIds = null)
    {
        replaceIds ??= EmptyIds;
        var keep = existing
            .Where(b => !(matchedIds.Contains(b.CanId) && !selectedIds.Contains(b.CanId))   // not a de-ticked match
                        && !replaceIds.Contains(b.CanId))                                     // not being replaced
            .ToList();
        var keepIds = keep.Select(b => b.CanId).ToHashSet();
        var added = incoming.Where(m => !keepIds.Contains(m.CanId));   // replaced ids fall through here -> incoming used
        return keep.Concat(added).OrderBy(b => b.CanId).ToList();
    }

    private static readonly HashSet<uint> EmptyIds = new();

    // Two broadcast messages share a "shape" when they would pack to the same wire frame: same DLC and
    // the same signal layout (name + start bit + length + byte order), order-independent. Message name
    // and per-signal value-source mappings are ignored - a pure rename or a remapped source is still
    // the same frame. The import reconcile uses this to tell a true re-import of the same message
    // (keep the existing row + its mappings) from a CAN-id collision where a different DBC reuses the
    // id for an unrelated message (offer the user keep/replace).
    public static bool SameShape(BroadcastMessage a, BroadcastMessage b)
    {
        if (a.Dlc != b.Dlc || a.Signals.Count != b.Signals.Count) return false;
        var ak = a.Signals.Select(SignalKey).OrderBy(k => k, StringComparer.Ordinal);
        var bk = b.Signals.Select(SignalKey).OrderBy(k => k, StringComparer.Ordinal);
        return ak.SequenceEqual(bk);
    }

    private static string SignalKey(BroadcastSignal s) => $"{s.Name}|{s.StartBit}|{s.Length}|{(int)s.ByteOrder}";

    // Best-effort name/unit heuristic mapping a DBC signal to a live engine SignalId. Conservative:
    // rate-of-change / input / output / turbine variants are deliberately left unmapped so they don't
    // masquerade as the primary signal. Returns null when nothing fits (-> a constant-0 field).
    public static SignalId? AutoMap(DbcSignal s)
    {
        string n = s.Name.ToLowerInvariant();
        string u = s.Unit.ToLowerInvariant();

        // Reject derivative / secondary speed variants up front.
        bool rate = n.Contains("roc") || n.Contains("rate") || n.Contains("_dt") || u.Contains("/s");

        if (!rate && u == "rpm" && Has(n, "engine") && Has(n, "speed")
            && !Has(n, "input") && !Has(n, "output") && !Has(n, "turbine") && !Has(n, "trans"))
            return SignalId.EngineRpm;

        if (!rate && (Has(n, "vehicle", "speed") || n.Contains("vskph") || n.Contains("vss") || u == "km/h" || u == "kph"))
            return SignalId.VehicleSpeed;

        if (Has(n, "coolant") || n.Contains("ect"))
            return SignalId.CoolantTemp;

        if (Has(n, "intake", "temp") || n.Contains("iat"))
            return SignalId.IntakeAirTemp;

        if (Has(n, "oil", "temp") || n.Contains("eot"))
            return SignalId.EngineOilTemp;

        if (!rate && Has(n, "throttle", "position") && !n.Contains("pedal"))
            return SignalId.ThrottlePosition;

        if (Has(n, "pedal") && (u == "%" || u.Length == 0))
            return SignalId.AcceleratorPedalPosition;

        if (n.Contains("maf") || Has(n, "mass", "air"))
            return SignalId.MassAirFlow;

        if (n.Contains("manifold") || (n.Contains("map") && u.Contains("kpa")))
            return SignalId.ManifoldAbsolutePressure;

        if (n.Contains("baro"))
            return SignalId.BarometricPressure;

        if (Has(n, "engine", "load") || n == "load")
            return SignalId.EngineLoad;

        if (n.Contains("vbat") || Has(n, "battery", "volt") || (u == "v" && n.Contains("volt")))
            return SignalId.ControlModuleVoltage;

        if (Has(n, "fuel", "level"))
            return SignalId.FuelLevel;

        if (Has(n, "timing", "advance") || Has(n, "spark", "advance"))
            return SignalId.TimingAdvance;

        return null;
    }

    private static bool Has(string haystack, params string[] tokens)
    {
        foreach (var t in tokens) if (!haystack.Contains(t)) return false;
        return true;
    }
}
