using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace Core.Services;

// $34 RequestDownload. GMW3110-2010 §8.12 (p156-160).
//
// Request: SID $34 + dataFormatIdentifier (1 byte) + unCompressedMemorySize (2..4 bytes).
//   dataFormatIdentifier: high nibble = compressionMethod, low nibble = encryptingMethod.
//                         $00 means no compression, no encryption (the only case we accept).
//   unCompressedMemorySize: 2-, 3- or 4-byte big-endian unsigned size.
// Positive response: $74 (no data parameters).
//
// NRCs (§8.12.4, Table 135):
//   $12 SFNS-IF        - length wrong, dataFormatIdentifier unsupported, or memorySize invalid
//   $22 CNCRSE         - $28 not active, $A5 not active, security locked, or download in progress
//   $78 RCR-RP         - cannot process within P2C (we never need this)
//   $99 ReadyForDownload-DTCStored - flash/EEPROM checksum DTC set (we never set this)
//
// "Download in progress" is NOT used as a gate. A new $34 is always accepted
// as long as the GMW3110 session preconditions ($28 / $A5 / $27 unlock) hold:
// the host has decided to start a new transfer, so any prior bracketed
// transfer is treated as naturally complete. This matches every real GM
// dealer-tool flow we've observed:
//   * 6Speed.T43's Pushspskernel issues two $34s (one per kernel section)
//     with declared size matching the actual section size - either rule
//     would accept the second $34 fine.
//   * Real DPS / TIS2WEB utility files declare `dataBytesPerMessage` (often
//     $0FFE = 4094) in every $34 as a *buffer hint*, then $36 transfers the
//     actual cal-file payload which is usually much smaller than $0FFE
//     (cal files run 100 B .. 200 KB). The simulator must not interpret
//     "declared > actual" as "still in progress" - DPS will fire the next
//     $34 to start the next cal as soon as it finishes the previous $36.
//   * The next $34 reallocates the sink buffer at the new declared size
//     (line ~130 below), discarding any partial state from the prior
//     section - so there's no risk of cross-section contamination.
//
// Spec mode: each $34 reallocates the sink buffer at the new declared size.
// Capture mode: each $34 marks a NEW logical transfer (per GMW3110 - one
// $34 brackets one logical "download"). On the first $34 of a session we
// just allocate a buffer. On every subsequent $34 we flush the current
// buffer to a .bin file (via BootloaderCaptureWriter), bump the session
// sequence counter, then allocate a fresh buffer for the new transfer.
// EcuExitLogic flushes the FINAL bin at session end ($20 or P3C timeout).
// Result: Pushspskernel ($34/$36, $34/$36) produces two .bin files, one
// per kernel piece; Sendbin (one $34, many $36s) produces one .bin per
// $34-bracketed segment - matching host intent in both cases.
//
// We accept dataFormatIdentifier $00 (the spec-pure no-compression / no-encryption form used by the kernel-load
// $34) and $10 (the GM kernel-mode form DPS uses once the programming kernel is resident, where the 2-byte size
// field carries dataBytesPerMessage rather than a total download size). True compression and encryption stay out
// of scope - $10 is treated as a buffer-size declaration, not as "compressed payload follows".
public static class Service34Handler
{
    /// <summary>
    /// Defensive upper bound on the buffer this simulator will allocate for an
    /// incoming download. 16 MiB covers every realistic GMW3110 ECU calibration
    /// + bootloader image; rejecting larger sizes prevents a malformed (or
    /// hostile) host from triggering a multi-GB allocation. GMW3110 doesn't
    /// have a dedicated NRC for "buffer too large", so we map the violation to
    /// $22 ConditionsNotCorrect (which §8.12.4 lists generically for "ECU not
    /// in a state to accept this request").
    /// </summary>
    public const uint MaxDownloadBufferBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Returns true if a positive response was sent (caller activates P3C),
    /// false if an NRC was sent.
    /// </summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch)
    {
        // Total length: 1 SID + 1 dataFormatIdentifier + (2..4) memorySize bytes = 4..6 bytes.
        if (usdtPayload.Length < 4 || usdtPayload.Length > 6 || usdtPayload[0] != Service.RequestDownload)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.RequestDownload, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        byte dataFormatIdentifier = usdtPayload[1];
        // Accept $00 (the spec-pure "no compression, no encryption" form used for the kernel-load $34) and $10 (the
        // GM kernel-mode form DPS sends once the bootloader-supplied programming kernel is resident). DPS uses $10
        // with a 2-byte size field as a dataBytesPerMessage declaration, not as a total download size - the value is
        // a per-$36-message buffer hint (typically $0FFE = 4094 bytes). Functionally the simulator handles both the
        // same way: allocate a sink buffer at the declared size and let the $36 stream do the real work. Without
        // this, real DPS hits NRC $12 at the post-kernel cal-block phase and bails to ReturnToNormal.
        if (dataFormatIdentifier != 0x00 && dataFormatIdentifier != 0x10)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.RequestDownload, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        // Decode the unCompressedMemorySize - the remaining 2..4 bytes BE.
        int sizeByteCount = usdtPayload.Length - 2;
        uint declaredSize = 0;
        for (int i = 0; i < sizeByteCount; i++)
            declaredSize = (declaredSize << 8) | usdtPayload[2 + i];

        if (declaredSize == 0)
        {
            // Zero-byte download is invalid per the implicit contract that $36 must
            // transfer at least one data byte.
            ServiceUtil.EnqueueNrc(node, ch, Service.RequestDownload, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        if (declaredSize > MaxDownloadBufferBytes)
        {
            // Over the simulator's resource cap; reject with $22 CNCRSE so the
            // host knows the ECU can't process this size right now (vs $12
            // which implies the value is structurally invalid).
            ServiceUtil.EnqueueNrc(node, ch, Service.RequestDownload, Nrc.ConditionsNotCorrectOrSequenceError);
            return false;
        }

        // §8.12.4 NRC $22 CNCRSE preconditions: $28 active, $A5 active, $27
        // unlocked. "Download in progress" is NOT checked here - see the
        // file-header comment for why; the buffer realloc below naturally
        // discards any partial prior-section state.
        if (!node.State.NormalCommunicationDisabled
            || !node.State.ProgrammingModeActive
            || node.State.SecurityUnlockedLevel == 0)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.RequestDownload, Nrc.ConditionsNotCorrectOrSequenceError);
            return false;
        }

        // Bracket close: if a prior $34/$36 stream already ran in this
        // session, the next $34 marks its completion. Sniff the assembled
        // bracket for PowerPC code BEFORE the realloc below blows it away;
        // if it looks like a kernel, dump a tagged copy. No-op when capture
        // dir isn't set or sniffer returns negative.
        if (node.State.DownloadActive && node.State.DownloadBuffer is not null
            && ch.Bus is not null)
        {
            BootloaderCaptureWriter.WriteCompletedBracketIfKernel(node, ch.Bus, "next34");
        }

        // Allocate a fresh sink buffer for this $34. Each $36 writes its own
        // .bin immediately, so $34 is purely a precondition + buffer-reset
        // gate. Session timestamp + sequence counter for capture filenames
        // are pinned by BootloaderCaptureWriter on the first $36 call.
        // The address-byte count for subsequent $36s is fixed once $34 has
        // been accepted; the spec (§8.13.2 Note 1) says all $36 to the same
        // node use the same number of address bytes.
        node.State.DownloadDeclaredSize = declaredSize;
        node.State.DownloadBuffer = new byte[declaredSize];
        node.State.DownloadBytesReceived = 0;
        node.State.DownloadCaptureBaseAddress = null;
        node.State.DownloadCaptureHighWaterMark = 0;
        node.State.DownloadActive = true;

        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(Service.RequestDownload)]);
        return true;
    }
}
