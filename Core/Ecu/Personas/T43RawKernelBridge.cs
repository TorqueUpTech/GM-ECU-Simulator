using Core.Bus;

namespace Core.Ecu.Personas;

// Bus-level handler for the RAW (non-ISO-TP) 6Speed T43 kernel protocol -- BOTH
// the read-kernel upload/read (served afterwards by T43KernelPersona) AND the
// write-kernel upload + segment-block streaming (the Can-Display dash's
// T43WriteClient / 6Speed.T43 Pushspskernel + Sendbin).
//
// The 6Speed T43 tool does NOT speak ISO-TP for kernel transfer: it sends a $36
// TransferData First Frame that declares a length, then streams the payload as
// bare 8-byte frames with no Consecutive-Frame PCI, and waits for specific
// single-frame acks the sim's reassembler/fragmenter can't produce. So the raw
// stream is intercepted here, from VirtualBus.DispatchHostTx, before the ISO-TP
// path. Anything unrecognised falls through (returns false) to the normal
// Gmw3110 dispatch -- $10/$27/$28/$A5 and every $34 RequestDownload still run
// their real handlers (Service34Handler answers $74, etc.).
//
// Wire sequences (requests on the TCM request CAN ID, responses on UsdtResponseCanId):
//
//  READ (T43Client uploadReadKernel + readBlock):
//    FF  1C EA 36 80 00 3F C4 30  -> FC 30 00 00 + 01 99 (kernel running); swallow
//                                    0xCEA-6 raw kernel bytes; swap in T43KernelPersona,
//                                    which then serves $35 memory reads.
//
//  WRITE (T43WriteClient pushSpsKernel + sendbin):
//    $34 05 34 00 00 0C 20        -> $74  (Service34Handler; falls through)
//    FF  1C 26 36 00 00 3F AF E0  -> 01 01 (addr ack) + 01 76 (post-stream ack, buffered);
//                                    swallow raw kernel part 1 until the next $34
//    $34 04 34 00 04 00           -> $74  (the $04 34 ends the swallow; falls through)
//    FF  14 06 36 00 00 3F BC 00  -> 01 01 + 01 76; swallow raw kernel part 2
//    $34 04 34 00 0F F6           -> $74  (ends the swallow; falls through)
//    FF  1F F6 36 00 00 3F C0 00  -> 01 01; count blockLen bytes of raw CFs...
//    CFs 21..2F [7 data]          -> on the last CF: 01 76 (block complete)
//    ...repeat descriptor+CFs per 4080-byte block, per Cal/Diag/System segment...
//    101 FE 01 20                 -> functional $20 (EcuExitLogic resets all raw state)
//
// The kernel byte counts don't match the FF-declared sizes (3552 vs 0xC26, 1176
// vs 0x406), so a write-kernel swallow can't be counted -- it ends when the next
// $34 arrives, and the "76" the tool reads after the stream is buffered ahead of
// it on the address FF. Block sizes ARE recoverable from the descriptor, so the
// block CF stream is counted down exactly and "0176" emitted on the last CF.
public static class T43RawKernelBridge
{
    // true  = handled here; VirtualBus should return without ISO-TP dispatch.
    // false = fall through to the reassembler / Gmw3110 dispatch.
    public static bool TryHandle(EcuNode node, ReadOnlySpan<byte> data, ChannelSession ch)
    {
        if (data.Length < 2) return false;
        var st = node.State;
        uint respId = node.UsdtResponseCanId;

        // 1. READ kernel-upload swallow. The $35 read command (PCI $07) marks the
        //    end of the raw kernel stream -> fall through so T43KernelPersona serves it.
        if (st.T43UploadRemaining > 0)
        {
            if (data[0] == 0x07 && data[1] == 0x35) { st.T43UploadRemaining = 0; return false; }
            st.T43UploadRemaining -= data.Length;   // swallow a raw kernel frame
            return true;
        }

        // 2. WRITE kernel-part swallow. The next $34 ($04 34) ends it (the "76" the
        //    tool waits for after the stream was buffered on the address FF below).
        if (st.T43WriteKernelSwallow)
        {
            if (data[0] == 0x04 && data[1] == 0x34) { st.T43WriteKernelSwallow = false; return false; }
            return true;   // swallow a raw kernel byte-frame
        }

        // 3. WRITE segment-block CF stream. Each CF carries 7 data bytes after its
        //    1-byte sequence PCI (last frame zero-padded); when the declared blockLen
        //    is consumed, emit the "0176" completion the tool loops waiting for.
        if (st.T43WriteBlockRemaining > 0)
        {
            st.T43WriteBlockRemaining -= data.Length - 1;   // strip the 1-byte CF PCI
            if (st.T43WriteBlockRemaining <= 0)
            {
                st.T43WriteBlockRemaining = 0;
                VirtualBus.EnqueueRawFrame(ch, respId, [0x01, 0x76]);
            }
            return true;
        }

        // 4. $36 TransferData First Frames (8 bytes, data[2]==$36). The 6Speed tool
        //    HIJACKS the ISO-TP First-Frame slot: its descriptors carry a
        //    declared-length field (data[0..1]) that a stock ISO-TP FF also uses. To
        //    avoid swallowing a GENUINE multi-frame $36 whose FF happens to share the
        //    same $36/address bytes (e.g. a 14-byte ISO-TP $36 to 0x3FAFE0 is
        //    "10 0E 36 00 00 3F AF E0"), each descriptor is matched on its EXACT
        //    declared-length prefix, and the variable-length block descriptor is
        //    gated on an active write session.
        if (data.Length == 8 && data[2] == 0x36 && data[3] == 0x00 && data[4] == 0x00
            && data[5] == 0x3F)
        {
            // WRITE kernel part 1: 1C 26 36 00 00 3F AF E0 (declares 0xC26).
            if (data[0] == 0x1C && data[1] == 0x26 && data[6] == 0xAF && data[7] == 0xE0)
                return BeginWriteKernelPart(st, ch, respId);

            // WRITE kernel part 2: 14 06 36 00 00 3F BC 00 (declares 0x406).
            if (data[0] == 0x14 && data[1] == 0x06 && data[6] == 0xBC && data[7] == 0x00)
                return BeginWriteKernelPart(st, ch, respId);

            // WRITE segment-block descriptor: 1X XX 36 00 00 3F C0 00, only mid-write.
            // blockLen is encoded b = ((len>>8)&0xF)+0x10, b2 = (len&0xFF)+6 -- inverted
            // here (recovers len even when b2 overflowed past 0xFF).
            if (st.T43WriteActive && (data[0] & 0xF0) == 0x10 && data[6] == 0xC0 && data[7] == 0x00)
            {
                st.T43WriteBlockRemaining = (((data[0] - 0x10) & 0x0F) << 8) | ((data[1] - 6) & 0xFF);
                VirtualBus.EnqueueRawFrame(ch, respId, [0x01, 0x01]);   // descriptor ack ("01")
                return true;
            }
        }

        // READ descriptor: 1C EA 36 80 00 3F C4 30 -> Flow Control + kernel-running,
        // swallow the read kernel, hand $35 reads to T43KernelPersona. (data[3]==0x80,
        // distinct from the write FFs' data[3]==0x00, so checked separately.)
        if (data.Length == 8 && data[0] == 0x1C && data[1] == 0xEA && data[2] == 0x36
            && data[3] == 0x80 && data[5] == 0x3F && data[6] == 0xC4 && data[7] == 0x30)
        {
            int total = ((data[0] & 0x0F) << 8) | data[1];        // 0xCEA = 3306
            st.T43UploadRemaining = total - (data.Length - 2);    // bytes after the FF's first 6
            VirtualBus.EnqueueRawFrame(ch, respId, [0x30, 0x00, 0x00]);  // Flow Control
            VirtualBus.EnqueueRawFrame(ch, respId, [0x01, 0x99]);        // kernel running
            node.Persona = T43KernelPersona.Instance;
            return true;
        }

        return false;
    }

    // A write-kernel part address FF: mark the write session active, start swallowing
    // the raw kernel byte stream, ack the address transaction ("01"), and buffer the
    // post-stream "76" ahead of the stream (the tool reads it after streaming).
    private static bool BeginWriteKernelPart(NodeState st, ChannelSession ch, uint respId)
    {
        st.T43WriteActive = true;
        st.T43WriteKernelSwallow = true;
        VirtualBus.EnqueueRawFrame(ch, respId, [0x01, 0x01]);
        VirtualBus.EnqueueRawFrame(ch, respId, [0x01, 0x76]);
        return true;
    }
}
