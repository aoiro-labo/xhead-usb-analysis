// Find the function containing "unhandled command : [%d]" (the master msServiceCmd dispatcher
// in mnbridge.cc), decompile it to see the actual switch/if-chain, and also locate whichever
// function handles cmd==28 (CmdProgramApply) specifically.
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Data;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.mem.Memory;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.PrintWriter;
import java.io.FileWriter;
import java.util.HashSet;
import java.util.Set;

public class XHeadFindDispatch extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_dispatch.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        // Search .rdata for the exact string and find its address via Ghidra's defined data / string search.
        String needle = "unhandled command : [%d]";
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
                out.println("=== Dispatcher function: " + f.getName() + " @ " + f.getEntryPoint() + " ===");
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
        println("Wrote dispatch analysis to " + outPath);
    }

    private Address findStringAddress(String needle) throws Exception {
        Memory mem = currentProgram.getMemory();
        byte[] pattern = (needle + "\0").getBytes("US-ASCII");
        Address start = currentProgram.getMinAddress();
        Address end = currentProgram.getMaxAddress();
        Address cur = start;
        while (cur != null && cur.compareTo(end) < 0) {
            Address hit = mem.findBytes(cur, end, pattern, null, true, monitor);
            if (hit == null) return null;
            return hit;
        }
        return null;
    }
}
