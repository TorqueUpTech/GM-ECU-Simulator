namespace Common.Protocol;

// Which diagnostic service the simulator should treat a Pid row as belonging to.
// One row, one mode - the dispatcher routes the request based on this enum.
//
//   Mode22: GMW3110 / UDS $22 ReadDataByParameterIdentifier. Pid.Address is the
//           2-byte wire PID id (0x0000..0xFFFF). Default - matches the legacy
//           single-mode behaviour, so v1..v14 configs deserialise unchanged.
//   Mode1A: GMW3110 $1A ReadDataByIdentifier. Pid.Address holds the 1-byte DID
//           in the low 8 bits (e.g. 0x0090 = DID $90 VIN). Response is the
//           Pid's StaticBytes payload; waveform fields are ignored.
//   Mode2D: GMW3110 $2D DefinePidByAddress, pre-baked at boot. Pid.Address holds
//           the 32-bit memory address the row mirrors; the wire PID id the
//           tester reads it with is derived deterministically as
//           0xF000 | (Address & 0x0FFF) - see Pid.WireLookupId. The 0xF000
//           range is GM's convention for dynamically-defined PIDs.
//
// OBD-II Mode $01 is NOT a Pid row: it is the built-in J1979 projection over the signal layer (see J1979Catalogue /
// EcuNode.Mode1Supported / Service01Handler). The old free-form Mode1 PID row was removed in the signal-centric clean
// break.
public enum PidMode
{
    Mode22 = 0,
    Mode1A = 1,
    Mode2D = 2,
}
