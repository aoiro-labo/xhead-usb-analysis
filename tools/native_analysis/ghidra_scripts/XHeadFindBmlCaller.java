// 2026-07-26 follow-up: FUN_1400a56f0 (mmts_bml.cc) only checks fopen() succeeds/fails -- doesn't
// parse the .xbml binary format itself. Find ITS caller(s) to locate the real content parser,
// which presumably runs right after a successful existence check.
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

public class XHeadFindBmlCaller extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_bml_caller.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        Address target = toAddr(0x1400a56f0L);
        out.println("=== Callers of FUN_1400a56f0 (BML file existence check) ===");

        Set<Function> seen = new HashSet<>();
        ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(target);
        int count = 0;
        int rawRefs = 0;
        while (refs.hasNext()) {
            Reference ref = refs.next();
            rawRefs++;
            Function f = getFunctionContaining(ref.getFromAddress());
            if (f != null && !seen.contains(f) && count < 10) {
                seen.add(f);
                count++;
                out.println("--- Caller: " + f.getName() + " @ " + f.getEntryPoint() + " (ref from " + ref.getFromAddress() + ") ---");
                DecompileResults res = decomp.decompileFunction(f, 150, new ConsoleTaskMonitor());
                if (res != null && res.decompileCompleted()) {
                    out.println(res.getDecompiledFunction().getC());
                } else {
                    out.println("Decompile failed: " + (res != null ? res.getErrorMessage() : "null"));
                }
                out.println();
            }
        }
        out.println("Total raw refs: " + rawRefs + ", distinct callers decompiled: " + count);

        decomp.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
