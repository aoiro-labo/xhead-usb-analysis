// Find the "slice buffer size" log string (from mslicebuffer.cc) and decompile the function that
// logs it, plus any nearby functions that construct/write individual slices, to understand the
// bulk transfer's mysterious 3-byte per-transfer prefix statically (no live capture needed).
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.mem.Memory;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.PrintWriter;
import java.io.FileWriter;
import java.util.HashSet;
import java.util.Set;

public class XHeadFindSliceBuffer extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_slicebuffer.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        String needle = "slice buffer size";
        Address found = findStringAddress(needle);
        if (found == null) {
            out.println("Could not locate string: " + needle);
            out.close();
            return;
        }
        out.println("Found string at: " + found);

        Set<Function> seen = new HashSet<>();
        ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(found);
        while (refs.hasNext()) {
            Reference ref = refs.next();
            Function f = getFunctionContaining(ref.getFromAddress());
            if (f != null && !seen.contains(f)) {
                seen.add(f);
                out.println("=== Function logging slice buffer size: " + f.getName() + " @ " + f.getEntryPoint() + " ===");
                DecompileResults res = decomp.decompileFunction(f, 180, new ConsoleTaskMonitor());
                if (res != null && res.decompileCompleted()) {
                    out.println(res.getDecompiledFunction().getC());
                } else {
                    out.println("Decompile failed: " + (res != null ? res.getErrorMessage() : "null"));
                }
                out.println();
            }
        }

        decomp.dispose();
        out.close();
        println("done -> " + outPath);
    }

    private Address findStringAddress(String needle) throws Exception {
        Memory mem = currentProgram.getMemory();
        byte[] pattern = needle.getBytes("US-ASCII");
        Address start = currentProgram.getMinAddress();
        Address end = currentProgram.getMaxAddress();
        return mem.findBytes(start, end, pattern, null, true, monitor);
    }
}
