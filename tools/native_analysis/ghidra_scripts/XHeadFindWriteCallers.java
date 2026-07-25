// Find callers of the single-write (FUN_140088500) and block-write (FUN_1400883b0) helpers to
// see the actual mhal_modulation.cc logic that assigns specific register addresses to specific
// modulation parameters (e.g. where 0x1202/0x1220/0x1221/0x1229/0x1290 come from as literals).
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
import java.util.HashSet;
import java.util.Set;

public class XHeadFindWriteCallers extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_write_callers.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        long[] targets = new long[] { 0x140088500L, 0x1400883b0L, 0x140087920L, 0x140087720L };
        String[] names = new String[] { "single-write helper", "block-write helper", "single-read helper", "block-read helper" };

        for (int i = 0; i < targets.length; i++) {
            Address target = toAddr(targets[i]);
            out.println("=== Callers of " + names[i] + " (0x" + Long.toHexString(targets[i]) + ") ===");

            Set<Function> seen = new HashSet<>();
            ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(target);
            int count = 0;
            while (refs.hasNext() && count < 30) {
                Reference ref = refs.next();
                Function f = getFunctionContaining(ref.getFromAddress());
                if (f != null && !seen.contains(f)) {
                    seen.add(f);
                    count++;
                    out.println("--- Caller: " + f.getName() + " @ " + f.getEntryPoint() +
                        " (ref from " + ref.getFromAddress() + ") ---");
                    DecompileResults res = decomp.decompileFunction(f, 150, new ConsoleTaskMonitor());
                    if (res != null && res.decompileCompleted()) {
                        out.println(res.getDecompiledFunction().getC());
                    } else {
                        out.println("Decompile failed: " + (res != null ? res.getErrorMessage() : "null"));
                    }
                    out.println();
                }
            }
            out.println("Total distinct callers: " + count);
            out.println();
        }

        decomp.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
