// 2026-07-26: Ghidra didn't auto-demangle EPG-related function names, so fall back to the
// proven technique from the BML investigation: find raw string references ("mepg_simple.cc",
// which appears in embedded __FILE__-style log/assert strings) and walk to their referencing
// functions.
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

public class XHeadFindEpgHandler extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_epg_handler.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        Memory mem = currentProgram.getMemory();
        byte[] pattern = "mepg_simple.cc".getBytes("UTF-8");
        Address start = currentProgram.getMinAddress();

        Set<Function> seen = new HashSet<>();
        Address found = mem.findBytes(start, pattern, null, true, monitor);
        int stringHits = 0;
        while (found != null) {
            stringHits++;
            out.println("string @ " + found);
            ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(found);
            while (refs.hasNext()) {
                Reference ref = refs.next();
                Function f = getFunctionContaining(ref.getFromAddress());
                if (f != null) seen.add(f);
            }
            Address next = found.add(1);
            if (next.compareTo(currentProgram.getMaxAddress()) >= 0) break;
            found = mem.findBytes(next, pattern, null, true, monitor);
        }
        out.println("Total string occurrences: " + stringHits + ", distinct referencing functions: " + seen.size());
        out.println();

        int count = 0;
        for (Function f : seen) {
            if (count >= 15) break;
            count++;
            out.println("=== " + f.getName() + " @ " + f.getEntryPoint() + " ===");
            DecompileResults res = decomp.decompileFunction(f, 150, new ConsoleTaskMonitor());
            if (res != null && res.decompileCompleted()) {
                String c = res.getDecompiledFunction().getC();
                // Trim very long functions to keep the report readable; full output still useful.
                out.println(c.length() > 6000 ? c.substring(0, 6000) + "\n...[truncated]..." : c);
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
