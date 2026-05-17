using System.Text.Json;
using Common.Protocol;

namespace Core.Security.Modules;

// Permissive 5-byte SecurityAccess module. Use this when DPS / SPS picks an
// Enhanced (5-byte) Seed utility file whose seed/key algorithm is server-
// brokered on the host side (PSA via ivcspsa.dll::GetPsaKey, or the IECS
// unlock-gmna-* HTTPS endpoint in iecs.dll::getKeyProvResponse) and therefore
// not computable from the simulator's side. The wire trace looks like a
// normal $27 handshake; nothing is actually verified.
//
// What it does:
//   $27 01            -> $67 01 SS SS SS SS SS   (non-zero random 5-byte seed)
//   $27 02 KK..       -> $67 02                  (any 1..16-byte key accepted)
//
// Why a separate module rather than reusing gm-programming-bypass:
//   gm-programming-bypass issues an all-zero 2-byte seed (BypassAll). DPS
//   rejects that for Enhanced 5-byte utility files with the message
//   "Error - Utility file is designed for an Enhanced Seed (5-Byte)!" and
//   "Error - The ECU did not return a 5-Byte seed!" - see
//   C:\DPS\Logs\ProgramLog_5 byte.Txt line 52-54 against archive
//   1GCUKREC6JK100001.zip / utility 12665853.BIN. The Enhanced flow
//   unconditionally demands a real 5-byte seed; the 2-byte "already
//   unlocked" convention does not exist for it.
//
// Limits:
//   * Real security is NOT enforced. Don't pick this if the test is about
//     verifying the ECU's $27 enforcement. It exists to keep the DPS host
//     flow moving so we can exercise everything downstream of $27.
//   * Level 1 only. Enhanced 5-byte production flow we've seen is level 1.
//   * Programming-session-aware bypass (the SecurityProgrammingShortcutActive
//     shortcut used by gm-programming-bypass) is not honoured - the module is
//     already permissive in every state, so the shortcut would be a no-op.
//
// Optional config (SecurityModuleConfig JsonElement):
//     { "fixedSeed": "AABBCCDDEE" }   // hex, 10 chars - deterministic seed for
//                                     //                 repeatable testing
public sealed class Gmw3110Permissive5Byte : ISecurityAccessModule
{
    private const byte SeedLen = 5;
    private const int MaxKeyLen = 16;

    private byte[]? fixedSeed;

    public string Id => "gm-permissive-5byte";

    public ProgrammingSessionBehavior ProgrammingSession => ProgrammingSessionBehavior.UnchangedAlgorithm;

    public void LoadConfig(JsonElement? config)
    {
        fixedSeed = null;
        if (config is null || config.Value.ValueKind != JsonValueKind.Object) return;
        if (!config.Value.TryGetProperty("fixedSeed", out var prop)) return;
        if (prop.ValueKind != JsonValueKind.String) return;
        var hex = prop.GetString();
        if (string.IsNullOrWhiteSpace(hex)) return;
        var s = hex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length != SeedLen * 2) return;
        var bytes = new byte[SeedLen];
        for (int i = 0; i < SeedLen; i++)
        {
            if (!byte.TryParse(s.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                               System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
                return;
        }
        fixedSeed = bytes;
    }

    public void Handle(SecurityAccessContext ctx)
    {
        var payload = ctx.UsdtPayload;
        if (payload.Length < 2 || payload[0] != Service.SecurityAccess)
        {
            ctx.Egress.SendNegativeResponse(Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }
        byte sub = payload[1];
        if (sub == 0x00 || sub == 0x7F)
        {
            ctx.Egress.SendNegativeResponse(Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }

        bool isRequestSeed = (sub & 0x01) == 1;
        byte level = (byte)((sub + 1) >> 1);

        // DPS Enhanced 5-byte flow is level 1. Reject other levels rather
        // than silently accept - a tester asking for level 2 on this module
        // is almost certainly misconfigured.
        if (level != 1)
        {
            ctx.Egress.SendNegativeResponse(Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }

        var state = ctx.State;
        lock (state.Sync)
        {
            if (isRequestSeed) HandleRequestSeed(ctx, sub, level);
            else               HandleSendKey   (ctx, sub, level);
        }
    }

    private void HandleRequestSeed(SecurityAccessContext ctx, byte sub, byte level)
    {
        if (ctx.UsdtPayload.Length != 2)
        {
            ctx.Egress.SendNegativeResponse(Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }

        var state = ctx.State;

        // Spec convention: if already unlocked, requestSeed returns seed-all-zero
        // as the "no further authentication needed" marker. This is the one
        // place all-zero IS meaningful in Enhanced 5-byte, so emit it explicitly.
        if (state.IsUnlocked(level))
        {
            var zeros = new byte[SeedLen];
            ctx.Egress.SendPositiveResponse(sub, zeros);
            return;
        }

        var seed = fixedSeed is not null ? (byte[])fixedSeed.Clone() : new byte[SeedLen];
        if (fixedSeed is null)
        {
            Random.Shared.NextBytes(seed);
            // Defensive: never emit an all-zero seed - DPS would interpret it
            // as "already unlocked, skip sendKey" and our state would diverge
            // (we're not actually unlocked yet, so the next $34 would fail
            // with NRC $22 ConditionsNotCorrect).
            if (seed[0] == 0 && seed[1] == 0 && seed[2] == 0 && seed[3] == 0 && seed[4] == 0)
                seed[0] = 1;
        }

        state.SecurityPendingSeedLevel = level;
        state.SecurityLastIssuedSeed = seed;

        ctx.Channel.Bus?.LogSim?.Invoke(
            $"[$27 PERMISSIVE-5] ECU '{ctx.Node.Name}' requestSeed lvl={level} -> seed={BitConverter.ToString(seed).Replace("-", " ")}");
        ctx.Egress.SendPositiveResponse(sub, seed);
    }

    private void HandleSendKey(SecurityAccessContext ctx, byte sub, byte level)
    {
        int keyLen = ctx.UsdtPayload.Length - 2;
        if (keyLen < 1 || keyLen > MaxKeyLen)
        {
            ctx.Egress.SendNegativeResponse(Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }

        var state = ctx.State;
        if (state.SecurityPendingSeedLevel != level || state.SecurityLastIssuedSeed is null)
        {
            ctx.Egress.SendNegativeResponse(Nrc.ConditionsNotCorrectOrSequenceError);
            return;
        }

        // Accept any key. The algorithm is server-brokered on the host side
        // (PSA / IECS unlock-* HTTPS); we have no way to independently compute
        // the expected value, so verification is a no-op by design.
        state.SecurityUnlockedLevel = level;
        state.SecurityPendingSeedLevel = 0;
        state.SecurityLastIssuedSeed = null;
        state.SecurityFailedAttempts = 0;
        state.SecurityLockoutUntilMs = 0;

        ctx.Channel.Bus?.LogSim?.Invoke(
            $"[$27 PERMISSIVE-5] ECU '{ctx.Node.Name}' sendKey lvl={level} keyLen={keyLen} -> unlocked (any key accepted)");
        ctx.Egress.SendPositiveResponse(sub, ReadOnlySpan<byte>.Empty);
    }
}
