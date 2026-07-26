// Decompile the single-read register-bus helper (mnservice+0x87920, established in
// tools/usb_capture/README.md "続報"/"続報2" as the function behind bRequest=0x4A SET + 0x4E GET)
// together with its callers, to find the specific call site that issues the periodic
// streaming-time notify (the one whose wIndex argument comes from "channel struct +0x24" and
// increments by 256 between calls, per the live cdb capture already on record) -- purely static,
// no live device access, so it carries none of the timing-perturbation risk a live cdb capture
// during active streaming would.
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.PrintWriter;
import java.io.FileWriter;

public class XHeadDecodeStreamNotify extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_stream_notify.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        long targetOffset = 0x140087920L; // mnservice+0x87920, image base 0x140000000

        Address addr = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(targetOffset);
        Function func = getFunctionContaining(addr);
        out.println("=== Target VA 0x" + Long.toHexString(targetOffset) + " (mnservice+0x87920) ===");
        if (func == null) {
            out.println("  No function found containing this address.");
            decomp.dispose();
            out.close();
            println("Wrote analysis to " + outPath);
            return;
        }
        out.println("Function: " + func.getName() + " @ " + func.getEntryPoint());
        out.println("Signature: " + func.getSignature());
        out.println();

        DecompileResults res = decomp.decompileFunction(func, 120, new ConsoleTaskMonitor());
        if (res != null && res.decompileCompleted()) {
            out.println("--- Decompiled (the single-read helper itself) ---");
            out.println(res.getDecompiledFunction().getC());
        } else {
            out.println("Decompile failed: " + (res != null ? res.getErrorMessage() : "null result"));
        }
        out.println();

        out.println("--- ALL raw references to " + func.getName() + " entry point (any type, CALL or DATA) ---");
        Address entry = func.getEntryPoint();
        ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(entry);
        int refCount = 0;
        int callerCount = 0;
        java.util.Set<String> seen = new java.util.HashSet<>();
        while (refs.hasNext()) {
            Reference ref = refs.next();
            refCount++;
            out.println("Ref #" + refCount + ": from=" + ref.getFromAddress() + " type=" + ref.getReferenceType() +
                " isData=" + (getFunctionContaining(ref.getFromAddress()) == null));
            Function callerFunc = getFunctionContaining(ref.getFromAddress());
            if (callerFunc == null) continue;
            String key = callerFunc.getEntryPoint().toString();
            if (seen.contains(key)) continue; // decompile each distinct caller function only once
            seen.add(key);
            callerCount++;
            out.println("  -> Caller #" + callerCount + ": " + callerFunc.getName() + " @ " + callerFunc.getEntryPoint());
            DecompileResults cres = decomp.decompileFunction(callerFunc, 120, new ConsoleTaskMonitor());
            if (cres != null && cres.decompileCompleted()) {
                out.println(cres.getDecompiledFunction().getC());
            } else {
                out.println("  (caller decompile failed)");
            }
            out.println();
        }
        out.println("Total raw references: " + refCount + " / Total distinct in-function callers: " + callerCount);

        // If truly zero references exist at all, the function's address may only be taken (e.g.
        // stored in a vtable/function-pointer table) via a LEA/MOV whose operand Ghidra didn't
        // record as a Reference to this address at all in a way getReferencesTo surfaces (seen
        // before with mCalibration's reader, tools/native_analysis/README.md). As a fallback,
        // search the whole program's defined data for any 8-byte value equal to this function's
        // address (a function-pointer table entry would literally store the raw address).
        if (refCount == 0) {
            out.println();
            out.println("--- Zero references found; scanning defined data for raw pointer value 0x" +
                Long.toHexString(targetOffset) + " (possible vtable/fn-ptr table entry), 8-byte aligned steps only ---");
            long target = targetOffset;
            ghidra.program.model.mem.Memory mem = currentProgram.getMemory();
            int found = 0;
            long scanned = 0;
            for (ghidra.program.model.mem.MemoryBlock block : mem.getBlocks()) {
                if (!block.isInitialized() || !block.isRead()) continue;
                Address start = block.getStart();
                Address end = block.getEnd();
                Address a = start;
                while (a != null && a.compareTo(end) < 0 && found < 20) {
                    try {
                        long val = mem.getLong(a);
                        scanned++;
                        if (val == target) {
                            found++;
                            out.println("  Found raw pointer match at " + a + " (block " + block.getName() + ")");
                        }
                    } catch (Exception e) {
                        break; // ran off the end of this block
                    }
                    try { a = a.add(8); } catch (Exception e) { break; }
                }
            }
            out.println("  Scanned " + scanned + " 8-byte-aligned slots across all readable initialized blocks, found " + found + " raw pointer matches.");
        }
        out.println("=====================================");

        decomp.dispose();
        out.close();
        println("Wrote analysis to " + outPath);
    }
}
