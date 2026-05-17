using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Core.Dps;

// Ported from tools/dps_utility_builder/parse_utility_file.py. The Python
// parser was validated round-trip against real DPS archives, so its byte
// layout is the source of truth.
public static class UtilityFileParser
{
    private const int PtiHeaderSize = 0x64;
    private const int SpsHeaderSize = 24;
    private const int InstructionSize = 16;
    private const int RoutineHeaderSize = 6;

    private static readonly IReadOnlyDictionary<byte, string> _opCodeNames = new Dictionary<byte, string>
    {
        [0x01] = "SETUP_GLOBAL_VARIABLES",
        [0x10] = "INITIATE_DIAGNOSTIC_OPERATION",
        [0x14] = "CLEAR_DTCS",
        [0x1A] = "READ_DATA_BY_IDENTIFIER",
        [0x20] = "RETURN_TO_NORMAL_MODE",
        [0x22] = "READ_DATA_BY_PARAMETER_IDENTIFIER",
        [0x27] = "SECURITY_ACCESS",
        [0x34] = "REQUEST_DOWNLOAD",
        [0x3B] = "WRITE_DATA_BY_IDENTIFIER",
        [0x50] = "COMPARE_BYTES",
        [0x51] = "COMPARE_CHECKSUM",
        [0x53] = "COMPARE_DATA",
        [0x54] = "CHANGE_DATA",
        [0x56] = "INTERPRETER_IDENTIFIER",
        [0x84] = "SET_COMMUNICATIONS_PARAMETERS",
        [0xA2] = "REPORT_PROGRAMMED_STATE_AND_SAVE_RESPONSE",
        [0xAA] = "READ_DATA_BY_PACKET_IDENTIFIER",
        [0xAE] = "REQUEST_DEVICE_CONTROL",
        [0xB0] = "BLOCK_TRANSFER_TO_RAM",
        [0xEE] = "END_WITH_ERROR",
        [0xF1] = "SET_GLOBAL_MEMORY_ADDRESS",
        [0xF2] = "SET_GLOBAL_MEMORY_LENGTH",
        [0xF3] = "SET_GLOBAL_HEADER_LENGTH",
        [0xF4] = "IGNORE_RESPONSES_FOR_MILLISECONDS",
        [0xF5] = "OVERRIDE_MESSAGE_LENGTH",
        [0xF7] = "NO_OP",
        [0xF8] = "GOTO_FIELD_CONTINUATION",
        [0xFB] = "SET_AND_DECREMENT_COUNTER",
        [0xFC] = "DELAY_FOR_XX_SECONDS",
        [0xFD] = "RESET_COUNTER",
        [0xFF] = "END_WITH_SUCCESS",
    };

    public static IReadOnlyDictionary<byte, string> OpCodeNames => _opCodeNames;

    public static UtilityFile ParseFile(string path)
    {
        byte[] blob = File.ReadAllBytes(path);
        return Parse(blob);
    }

    public static UtilityFile Parse(ReadOnlySpan<byte> blob)
    {
        // Auto-detect a PTI wrapper by reading the u32 BE at offset 0. PTI
        // files carry a format-type magic of 1 or 2; a bare SPS body starts
        // with the 2-byte checksum which is highly unlikely to look like a
        // valid format type. Mirror of Python's has_pti probe.
        bool hasPti = blob.Length >= PtiHeaderSize + SpsHeaderSize
                      && BinaryPrimitives.ReadUInt32BigEndian(blob[..4]) is 1u or 2u;

        if (!hasPti)
        {
            if (blob.Length < SpsHeaderSize)
                throw new InvalidDataException(
                    $"File too short ({blob.Length} bytes) for SPS header at offset 0");

            var (sps, instructions, routines) = ParseSpsBody(blob, baseOffset: 0, dataLen: blob.Length);
            return new UtilityFile(null, sps, instructions, routines);
        }

        if (blob.Length < PtiHeaderSize + SpsHeaderSize)
            throw new InvalidDataException(
                $"File too short ({blob.Length} bytes) for PTI + SPS headers");

        uint formatType = BinaryPrimitives.ReadUInt32BigEndian(blob[0..4]);
        string partNo = DecodeAscii(blob.Slice(4, 36)).TrimEnd(' ', '\0');
        uint blockNo = BinaryPrimitives.ReadUInt32BigEndian(blob.Slice(40, 4));
        uint numBlocks = BinaryPrimitives.ReadUInt32BigEndian(blob.Slice(44, 4));
        string creationDate = DecodeAscii(blob.Slice(48, 14));
        byte dataType = blob[62];
        uint noOfAddressBytes = BinaryPrimitives.ReadUInt32BigEndian(blob.Slice(84, 4));
        uint noOfDataBytes = BinaryPrimitives.ReadUInt32BigEndian(blob.Slice(88, 4));
        uint crcType = BinaryPrimitives.ReadUInt32BigEndian(blob.Slice(92, 4));
        uint crcBytes = BinaryPrimitives.ReadUInt32BigEndian(blob.Slice(96, 4));

        var pti = new PtiHeader(
            formatType, partNo, blockNo, numBlocks, creationDate, dataType,
            noOfAddressBytes, noOfDataBytes, crcType, crcBytes);

        if (formatType != 1)
            throw new InvalidDataException(
                $"Unsupported PTI formatType {formatType} (only 1 is supported)");

        int dataLen = (int)noOfDataBytes;
        if (PtiHeaderSize + dataLen > blob.Length)
            throw new InvalidDataException(
                $"PTI noOfDataBytes ({dataLen}) overruns blob ({blob.Length - PtiHeaderSize} bytes remain after PTI)");

        var (sps2, instructions2, routines2) = ParseSpsBody(blob, baseOffset: PtiHeaderSize, dataLen: dataLen);
        return new UtilityFile(pti, sps2, instructions2, routines2);
    }

    private static (SpsInterpreterHeader Sps, IReadOnlyList<InterpreterInstruction> Instructions, IReadOnlyList<UtilityRoutine> Routines)
        ParseSpsBody(ReadOnlySpan<byte> blob, int baseOffset, int dataLen)
    {
        if (blob.Length < baseOffset + SpsHeaderSize)
            throw new InvalidDataException(
                $"File too short ({blob.Length} bytes) for SPS header at offset 0x{baseOffset:X}");

        ReadOnlySpan<byte> sps = blob.Slice(baseOffset, SpsHeaderSize);
        var header = new SpsInterpreterHeader(
            Checksum: BinaryPrimitives.ReadUInt16BigEndian(sps[0..2]),
            ModuleId: BinaryPrimitives.ReadUInt16BigEndian(sps[2..4]),
            UtilityPartNo: BinaryPrimitives.ReadUInt32BigEndian(sps[4..8]),
            DesignLevel: BinaryPrimitives.ReadUInt16BigEndian(sps[8..10]),
            HeaderType: BinaryPrimitives.ReadUInt16BigEndian(sps[10..12]),
            InterpType: BinaryPrimitives.ReadUInt16BigEndian(sps[12..14]),
            RoutineSectionOffset: BinaryPrimitives.ReadUInt16BigEndian(sps[14..16]),
            AddType: BinaryPrimitives.ReadUInt16BigEndian(sps[16..18]),
            DataAddressInfo: BinaryPrimitives.ReadUInt32BigEndian(sps[18..22]),
            DataBytesPerMessage: BinaryPrimitives.ReadUInt16BigEndian(sps[22..24]));

        int instSectionLen = header.RoutineSectionOffset - SpsHeaderSize;
        if (instSectionLen < 0 || instSectionLen % InstructionSize != 0)
            throw new InvalidDataException(
                $"Instruction-section length {instSectionLen} is not a non-negative multiple of {InstructionSize}");

        int numInstructions = instSectionLen / InstructionSize;
        int instBase = baseOffset + SpsHeaderSize;
        if (blob.Length < instBase + instSectionLen)
            throw new InvalidDataException(
                $"Instruction section ({numInstructions} × {InstructionSize}B) runs past end of blob");

        var instructions = new InterpreterInstruction[numInstructions];
        for (int i = 0; i < numInstructions; i++)
        {
            ReadOnlySpan<byte> raw = blob.Slice(instBase + i * InstructionSize, InstructionSize);
            instructions[i] = new InterpreterInstruction(
                Step: raw[0],
                OpCode: raw[1],
                Action: raw.Slice(2, 4).ToArray(),
                Gotos: raw.Slice(6, 10).ToArray());
        }

        int routineBase = baseOffset + header.RoutineSectionOffset;
        int routineEnd = baseOffset + dataLen;
        if (routineEnd > blob.Length)
            throw new InvalidDataException(
                $"Declared routine end (0x{routineEnd:X}) overruns blob length (0x{blob.Length:X})");
        if (routineBase > routineEnd)
            throw new InvalidDataException(
                $"routineSectionOffset (0x{header.RoutineSectionOffset:X}) extends past data section");

        var routines = new List<UtilityRoutine>();
        int cursor = routineBase;
        int idx = 0;
        while (cursor < routineEnd)
        {
            if (routineEnd - cursor < RoutineHeaderSize)
                throw new InvalidDataException(
                    $"Trailing {routineEnd - cursor} bytes at end of data section: not enough for a routine header");

            uint addr = BinaryPrimitives.ReadUInt32BigEndian(blob.Slice(cursor, 4));
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(blob.Slice(cursor + 4, 2));
            int dataStart = cursor + RoutineHeaderSize;
            if (dataStart + length > routineEnd)
                throw new InvalidDataException(
                    $"Routine[{idx}] length {length} at addr 0x{addr:X8} runs past end of data section");

            routines.Add(new UtilityRoutine(idx, addr, blob.Slice(dataStart, length).ToArray()));
            cursor = dataStart + length;
            idx++;
        }

        return (header, instructions, routines);
    }

    private static string DecodeAscii(ReadOnlySpan<byte> bytes)
    {
        return Encoding.ASCII.GetString(bytes);
    }
}
