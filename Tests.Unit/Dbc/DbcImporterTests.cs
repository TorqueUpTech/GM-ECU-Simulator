using Common.Dbc;
using Common.Protocol;
using Common.Signals;
using Core.Dbc;
using Core.Ecu;
using Xunit;

namespace EcuSimulator.Tests.Dbc;

// DbcImporter: auto-mapping heuristic, scoped (transmitter + id) import, and merge/replace semantics.
public sealed class DbcImporterTests
{
    private static string LocateResource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "resources", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate resources/" + fileName);
    }

    private static DbcSignal Sig(string name, string unit = "", int len = 16)
        => new() { Name = name, StartBit = 7, Length = len, ByteOrder = DbcByteOrder.Motorola, Scale = 1, Unit = unit };

    [Fact]
    public void AutoMap_MapsEngineSpeedAndVehicleSpeed_LeavesRateOfChangeUnmapped()
    {
        Assert.Equal(SignalId.EngineRpm, DbcImporter.AutoMap(Sig("Engine_Speed", "rpm")));
        Assert.Equal(SignalId.VehicleSpeed, DbcImporter.AutoMap(Sig("Vehicle_Speed", "km/h")));
        Assert.Null(DbcImporter.AutoMap(Sig("Engine_Speed_ROC", "rpm/s")));
        Assert.Equal(SignalId.CoolantTemp, DbcImporter.AutoMap(Sig("Engine_Coolant_Temp", "degC", 8)));
    }

    [Fact]
    public void ToBroadcasts_ScopesToTransmitterAndSelectedIds_AutoMapsRpm()
    {
        var db = DbcParser.Parse(File.ReadAllText(LocateResource("FG_Falcon_HighSpeed_CAN.dbc")));
        var msgs = DbcImporter.ToBroadcasts(db, "Vector__XXX", new HashSet<uint> { 519 });

        var msg = Assert.Single(msgs);
        Assert.Equal(519u, msg.CanId);
        Assert.Equal(DbcImporter.DefaultPeriodMs, msg.PeriodMs);   // FG file has no GenMsgCycleTime

        var rpm = msg.Signals.Single(s => s.Name == "Engine_Speed");
        Assert.Equal(BroadcastValueSource.Signal, rpm.ValueSource);
        Assert.Equal(SignalId.EngineRpm, rpm.Signal);
    }

    [Fact]
    public void TransmittersByMessageCount_OrdersByCountDescending()
    {
        var db = DbcParser.Parse(File.ReadAllText(LocateResource("GlobalA - HS.dbc")));
        var tx = DbcImporter.TransmittersByMessageCount(db);
        Assert.Equal("ECM_HS", tx[0].Transmitter);                 // most prolific on a GM HS bus
        Assert.True(tx[0].Count >= tx[1].Count);
    }

    [Fact]
    public void ReplaceBroadcasts_ClearsPriorSet()
    {
        var node = NodeNew();
        node.AddBroadcast(new BroadcastMessage { CanId = 0x111, Name = "old" });
        node.ReplaceBroadcasts(new[] { new BroadcastMessage { CanId = 0x222, Name = "new" } });

        var only = Assert.Single(node.Broadcasts);
        Assert.Equal(0x222u, only.CanId);
    }

    private static EcuNode NodeNew() => new() { Name = "ECM", PhysicalRequestCanId = 0x7E0, UsdtResponseCanId = 0x7E8, UudtResponseCanId = 0x5E8 };

    // ---- Reconcile (re-import into an ECU that already has broadcasts) ----

    private static BroadcastMessage Msg(uint id, string name = "m") => new() { CanId = id, Name = name };

    [Fact]
    public void Reconcile_RemovesDeselectedMatch_KeepsUnmatched_AppendsNew_SortedByCanId()
    {
        var existing = new[] { Msg(0x300, "keep"), Msg(0x200, "drop"), Msg(0x500, "untouched") };
        // 0x200 and 0x300 are same-id+same-shape matches (pre-ticked). 0x500 is not matched (the DBC
        // doesn't define it, or defines it differently) - it must never be removed here.
        var matched = new HashSet<uint> { 0x200, 0x300 };
        // User kept 0x300 ticked, de-ticked 0x200, ticked the new 0x400.
        var selected = new HashSet<uint> { 0x300, 0x400 };
        var incoming = new[] { Msg(0x300, "incoming-300"), Msg(0x400, "new") };

        var result = DbcImporter.Reconcile(existing, matched, selected, incoming);

        Assert.Equal(new uint[] { 0x300, 0x400, 0x500 }, result.Select(m => m.CanId).ToArray());
        // 0x200 dropped (matched + de-ticked); 0x500 kept (not matched); 0x400 appended.
        // 0x300 kept as the EXISTING row - its mappings are preserved, not overwritten by incoming.
        Assert.Equal("keep", result.Single(m => m.CanId == 0x300).Name);
        Assert.Equal("untouched", result.Single(m => m.CanId == 0x500).Name);
        Assert.Equal("new", result.Single(m => m.CanId == 0x400).Name);
    }

    [Fact]
    public void Reconcile_NoTicks_RemovesEveryMatchedRow()
    {
        var existing = new[] { Msg(0x100), Msg(0x200) };
        var matched = new HashSet<uint> { 0x100, 0x200 };
        var result = DbcImporter.Reconcile(existing, matched, new HashSet<uint>(), Array.Empty<BroadcastMessage>());
        Assert.Empty(result);
    }

    [Fact]
    public void Reconcile_CollisionLeftUnticked_KeepsExistingRow()
    {
        // 0xC9 exists with a different shape than the DBC's 0xC9 (a collision), so it is NOT matched
        // and is not pre-ticked. Left unticked it must be KEPT, not removed - the data-loss guard.
        var existing = new[] { Shaped(0xC9, 8, ("Ford", 7, 16)) };
        var result = DbcImporter.Reconcile(
            existing, matchedIds: new HashSet<uint>(), selectedIds: new HashSet<uint>(),
            incoming: Array.Empty<BroadcastMessage>());
        var only = Assert.Single(result);
        Assert.Equal(0xC9u, only.CanId);
        Assert.Equal("Ford", only.Signals.Single().Name);
    }

    // ---- SameShape (collision detection) ----

    private static BroadcastMessage Shaped(uint id, int dlc, params (string name, int bit, int len)[] sigs)
    {
        var m = new BroadcastMessage { CanId = id, Name = "m", Dlc = dlc };
        foreach (var (name, bit, len) in sigs)
            m.Signals.Add(new BroadcastSignal { Name = name, StartBit = bit, Length = len, ByteOrder = DbcByteOrder.Motorola });
        return m;
    }

    [Fact]
    public void SameShape_IdenticalLayout_True_RegardlessOfMessageNameAndSource()
    {
        var a = Shaped(0xC9, 8, ("Engine_Speed", 7, 16), ("Temp", 23, 8));
        var b = Shaped(0xC9, 8, ("Temp", 23, 8), ("Engine_Speed", 7, 16));   // order-independent
        b.Name = "renamed";
        b.Signals[0].ValueSource = BroadcastValueSource.Constant;            // source differs
        Assert.True(DbcImporter.SameShape(a, b));
    }

    [Fact]
    public void SameShape_DifferentShape_False()
    {
        var a = Shaped(0xC9, 8, ("Engine_Speed", 7, 16));
        Assert.False(DbcImporter.SameShape(a, Shaped(0xC9, 6, ("Engine_Speed", 7, 16))));            // DLC
        Assert.False(DbcImporter.SameShape(a, Shaped(0xC9, 8, ("Engine_Speed", 7, 8))));             // length
        Assert.False(DbcImporter.SameShape(a, Shaped(0xC9, 8, ("Vehicle_Speed", 7, 16))));           // signal name
        Assert.False(DbcImporter.SameShape(a, Shaped(0xC9, 8, ("Engine_Speed", 7, 16), ("X", 39, 8)))); // count
    }

    [Fact]
    public void Reconcile_ReplaceId_TakesIncoming_OtherCollisionKept()
    {
        // Two ticked ids that already exist. 0xC8 is a same-shape match; 0xC9 is a collision (the
        // import redefines it) the user chose to replace.
        var existing = new[] { Shaped(0xC9, 8, ("Ford", 7, 16)), Shaped(0xC8, 8, ("Keep", 7, 16)) };
        var matched = new HashSet<uint> { 0xC8 };               // 0xC9 differs in shape -> not matched
        var selected = new HashSet<uint> { 0xC8, 0xC9 };
        var incoming = new[] { Shaped(0xC9, 6, ("GM", 7, 8)), Shaped(0xC8, 8, ("Keep", 7, 16)) };
        var replace = new HashSet<uint> { 0xC9 };

        var result = DbcImporter.Reconcile(existing, matched, selected, incoming, replace);

        Assert.Equal(new uint[] { 0xC8, 0xC9 }, result.Select(m => m.CanId).ToArray());
        // 0x0C9 replaced -> incoming GM definition (6B); 0x0C8 not in replace -> existing kept.
        var c9 = result.Single(m => m.CanId == 0xC9);
        Assert.Equal(6, c9.Dlc);
        Assert.Equal("GM", c9.Signals.Single().Name);
        Assert.Equal("Keep", result.Single(m => m.CanId == 0xC8).Signals.Single().Name);
    }
}
