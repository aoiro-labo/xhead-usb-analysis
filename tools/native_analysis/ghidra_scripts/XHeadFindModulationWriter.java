// Find the sibling function to FUN_14039ba70 (RF power writer) that handles the ISDB-T
// modulation param fields (Constellation/Bandwidth/FFT/CodeRate/GuardInterval/TimeInterleave).
// Strategy: FUN_14009c540 (adjust power wrapper) was found via its "adjust power" log string in
// mpegasys_output.cc. Frequency's write path was found via a live cdb capture correlating a
// distinctive value. Try searching for OTHER mhal_modulation.cc / mpegasys_output.cc log strings
// that might mark a "set modulation" or "apply constellation" style function, and also look at
// what calls the SAME 0x1202-writing function (found via cdb earlier at a caller matching
// mnservice+0x87920's structure) to see if it's part of a bigger sequential-field-writer like
// FUN_14039ba70 was for RF power.
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.PrintWriter;
import java.io.FileWriter;
import java.util.HashSet;
import java.util.Set;

public class XHeadFindModulationWriter extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_modulation_writer.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        // Step 1: find all "mhal_modulation.cc" string occurrences to enumerate every function
        // in that source file that embeds a log call (a proxy for "every notable function in
        // this file", since almost every guarded branch logs on failure).
        Memory mem = currentProgram.getMemory();
        // References point to the START of the string literal, not an arbitrary substring
        // position -- use the full path prefix (seen verbatim in earlier decompiled output) so
        // findBytes lands exactly where Ghidra's xrefs actually point.
        byte[] pattern = "D:\\mn-next\\mnframework\\components\\service\\platform\\src\\mhal_modulation.cc".getBytes("US-ASCII");
        Address cur = currentProgram.getMinAddress();
        Address end = currentProgram.getMaxAddress();
        Set<Function> seen = new HashSet<>();
        int stringHits = 0;
        while (true) {
            Address found = mem.findBytes(cur, end, pattern, null, true, monitor);
            if (found == null) break;
            stringHits++;
            ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(found);
            while (refs.hasNext()) {
                Reference ref = refs.next();
                Function f = getFunctionContaining(ref.getFromAddress());
                if (f != null && !seen.contains(f)) {
                    seen.add(f);
                }
            }
            cur = found.add(1);
        }
        out.println("Found " + stringHits + " occurrences of \"mhal_modulation.cc\" string, referenced from " +
            seen.size() + " distinct functions:");
        for (Function f : seen) {
            out.println("  " + f.getName() + " @ " + f.getEntryPoint());
        }
        out.println();

        // Step 2: decompile each of those functions so we can eyeball for FUN_140088050 /
        // FUN_140088500 / FUN_1400883b0 call patterns with literal address constants (the
        // "write modulation field" signature we already know from the RF power writer).
        for (Function f : seen) {
            out.println("=== " + f.getName() + " @ " + f.getEntryPoint() + " ===");
            DecompileResults res = decomp.decompileFunction(f, 150, new ConsoleTaskMonitor());
            if (res != null && res.decompileCompleted()) {
                String c = res.getDecompiledFunction().getC();
                // Only print if it looks relevant (mentions a write helper or a 0x12xx-ish literal)
                out.println(c);
            } else {
                out.println("Decompile failed: " + (res != null ? res.getErrorMessage() : "null"));
            }
            out.println();
        }

        decomp.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
