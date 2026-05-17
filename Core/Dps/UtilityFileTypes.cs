namespace Core.Dps;

public sealed record PtiHeader(
    uint FormatType,
    string PartNo,
    uint BlockNo,
    uint NumBlocks,
    string CreationDate,
    byte DataType,
    uint NoOfAddressBytes,
    uint NoOfDataBytes,
    uint CrcType,
    uint CrcBytes);

public sealed record SpsInterpreterHeader(
    ushort Checksum,
    ushort ModuleId,
    uint UtilityPartNo,
    ushort DesignLevel,
    ushort HeaderType,
    ushort InterpType,
    ushort RoutineSectionOffset,
    ushort AddType,
    uint DataAddressInfo,
    ushort DataBytesPerMessage);

public sealed record InterpreterInstruction(
    byte Step,
    byte OpCode,
    byte[] Action,
    byte[] Gotos);

public sealed record UtilityRoutine(
    int Index,
    uint Address,
    byte[] Data);

public sealed record UtilityFile(
    PtiHeader? Pti,
    SpsInterpreterHeader Sps,
    IReadOnlyList<InterpreterInstruction> Instructions,
    IReadOnlyList<UtilityRoutine> Routines);
