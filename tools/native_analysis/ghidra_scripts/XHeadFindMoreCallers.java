// Find ALL callers of FUN_14039ba70 (the RF power writer) and FUN_14009c540 (the "adjust power"
// wrapper) to check: (1) are there other call sites to FUN_14039ba70 that might set the
// param_3+4 flag differently (revealing PAGain's real destination), and (2) is FUN_14009c540
// itself dispatched per-Mode (which would explain 0x1290's low nibble varying by Mode)?
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

public class XHeadFindMoreCallers extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_more_callers.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        long[] targets = new long[] { 0x14039ba70L, 0x14009c540L };
        String[] names = new String[] { "FUN_14039ba70 (RF power writer)", "FUN_14009c540 (adjust power wrapper)" };

        for (int i = 0; i < targets.length; i++) {
            Address target = toAddr(targets[i]);
            out.println("=== Callers of " + names[i] + " ===");

            Set<Function> seen = new HashSet<>();
            ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(target);
            int count = 0;
            int dataRefCount = 0;
            while (refs.hasNext() && count < 30) {
                Reference ref = refs.next();
                if (!ref.getReferenceType().isCall() && !ref.getReferenceType().isJump()) {
                    dataRefCount++;
                    out.println("(non-call/jump ref from " + ref.getFromAddress() + ", type=" + ref.getReferenceType() + ")");
                    continue;
                }
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
            out.println("Total distinct call-site callers: " + count + ", non-call refs: " + dataRefCount);
            out.println();
        }

        decomp.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
