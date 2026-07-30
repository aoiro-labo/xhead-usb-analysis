//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.FileWriter;
import java.io.PrintWriter;
import java.util.HashSet;
import java.util.LinkedHashSet;
import java.util.Set;

public class XHeadTraceSliceBufferCallers extends GhidraScript {
    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_slicebuffer_callers.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);

        Set<Function> level = new LinkedHashSet<>();
        Function allocator = getFunctionAt(toAddr(0x14038ac60L));
        if (allocator == null) {
            out.println("slice allocator function not found");
        } else {
            out.println("ROOT " + allocator.getName() + " @ " + allocator.getEntryPoint());
            level.add(allocator);
            Set<Function> emitted = new HashSet<>();
            for (int depth = 1; depth <= 3; depth++) {
                Set<Function> next = new LinkedHashSet<>();
                out.println("\n===== CALLER DEPTH " + depth + " =====");
                for (Function target : level) {
                    ReferenceIterator refs = currentProgram.getReferenceManager()
                        .getReferencesTo(target.getEntryPoint());
                    while (refs.hasNext()) {
                        Reference reference = refs.next();
                        if (!reference.getReferenceType().isCall()) continue;
                        Function caller = getFunctionContaining(reference.getFromAddress());
                        if (caller == null || !emitted.add(caller)) continue;
                        next.add(caller);
                        out.println("\n--- " + caller.getName() + " @ " + caller.getEntryPoint() +
                            " calls " + target.getName() + " from " + reference.getFromAddress() + " ---");
                        DecompileResults result = decompiler.decompileFunction(caller, 180,
                            new ConsoleTaskMonitor());
                        if (result != null && result.decompileCompleted())
                            out.println(result.getDecompiledFunction().getC());
                        else
                            out.println("Decompile failed: " +
                                (result == null ? "null" : result.getErrorMessage()));
                    }
                }
                level = next;
            }
        }
        decompiler.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
