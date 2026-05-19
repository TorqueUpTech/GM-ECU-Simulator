using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Transport;

namespace Core.Services;

// $01 ShowCurrentData per SAE J1979 (OBD-II Mode $01). Lives on the
// UDS-stack dispatcher on real GM silicon (see memory file
// project_dual_diag_stack_e38_e67.md - $01 is index 0 of the 27-entry
// UDS SID table at E38 $12c690 / E67 $133e84). The GMW3110 GMLAN-enhanced
// dispatcher does not implement it, so a tester hitting a GMW3110-only
// CAN ID gets the same NRC $11 a real ECU would emit.
//
// USDT request:
//   byte[0]      = 0x01
//   bytes[1..]   = N x 1-byte PID ids
//
// USDT positive response:
//   byte[0]      = 0x41
//   bytes[1..]   = K x { PID id, value bytes }, K = supported count
//
// Per J1979: multi-PID requests are supported; unsupported PIDs are
// silently dropped from the response; if NONE are supported a physical
// request gets NRC $31 ROOR and a functional broadcast stays silent
// (same convention as $22 in GMW3110 §8.6.4).
public static class Service01Handler
{
    public static void Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch,
                              double timeMs, bool isFunctional)
    {
        if (usdtPayload.Length < 2 || usdtPayload[0] != Service.Obd01ShowCurrentData)
        {
            if (!isFunctional)
                ServiceUtil.EnqueueNrc(node, ch, Service.Obd01ShowCurrentData,
                                        Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }

        int pidCount = usdtPayload.Length - 1;
        var supported = new List<(byte PidId, Pid Pid)>(pidCount);
        for (int i = 0; i < pidCount; i++)
        {
            byte pidId = usdtPayload[1 + i];
            var pid = node.GetMode1Pid(pidId);
            if (pid != null) supported.Add((pidId, pid));
        }

        if (supported.Count == 0)
        {
            if (!isFunctional)
                ServiceUtil.EnqueueNrc(node, ch, Service.Obd01ShowCurrentData, Nrc.RequestOutOfRange);
            return;
        }

        int respSize = 1;  // SID
        foreach (var (_, pid) in supported) respSize += 1 + pid.ResponseLength;
        var resp = new byte[respSize];
        resp[0] = Service.Positive(Service.Obd01ShowCurrentData);
        int pos = 1;
        foreach (var (pidId, pid) in supported)
        {
            resp[pos++] = pidId;
            int len = pid.ResponseLength;
            pid.WriteResponseBytes(timeMs, resp.AsSpan(pos, len));
            pos += len;
        }

        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, resp);
    }
}
