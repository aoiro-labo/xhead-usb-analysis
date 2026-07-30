//@category XHead

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.FileWriter;
import java.io.PrintWriter;
import java.util.HashSet;
import java.util.Set;

public class XHeadTraceTransferModerFactory extends GhidraScript {
    private PrintWriter out;
    private DecompInterface decompiler;
    private Set<Function> seen = new HashSet<>();

    @Override
    public void run() throws Exception {
        String outPath =
            "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_transfer_moder_factory.txt";
        out = new PrintWriter(new FileWriter(outPath));
        decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        trace(getFunctionAt(toAddr(0x140094f60L)), 0, 4);
        decompiler.dispose();
        out.close();
        println("done -> " + outPath);
    }

    private void trace(Function target, int depth, int maxDepth) {
        if (target == null || depth > maxDepth) return;
        out.println();
        out.println("===== DEPTH " + depth + " CALLERS OF " +
            target.getEntryPoint() + " " + target.getName() + " =====");
        ReferenceIterator refs =
            currentProgram.getReferenceManager().getReferencesTo(target.getEntryPoint());
        while (refs.hasNext()) {
            Reference reference = refs.next();
            if (!reference.getReferenceType().isCall() &&
                !reference.getReferenceType().isJump()) continue;
            Function caller = getFunctionContaining(reference.getFromAddress());
            if (caller == null || !seen.add(caller)) continue;
            out.println();
            out.println("--- " + caller.getEntryPoint() + " " + caller.getName() +
                " ref=" + reference.getFromAddress() + " ---");
            DecompileResults result = decompiler.decompileFunction(
                caller, 240, new ConsoleTaskMonitor());
            if (result.decompileCompleted()) {
                out.println(result.getDecompiledFunction().getC());
            } else {
                out.println("FAILED: " + result.getErrorMessage());
            }
            trace(caller, depth + 1, maxDepth);
        }
    }
}
