// Find and decompile every function referencing the second-stage
// "unsupported modulation mode" rejection reached by DVB-T2 after fixing
// FECBlockNums=0. Static analysis only; no device access.
//@category XHead

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

import java.io.FileWriter;
import java.io.PrintWriter;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashSet;
import java.util.Set;

public class XHeadFindUnsupportedModulation extends GhidraScript {
    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_unsupported_modulation.txt";
        byte[] needle = "unsupported modulation mode".getBytes(StandardCharsets.US_ASCII);
        Memory memory = currentProgram.getMemory();
        Address cursor = currentProgram.getMinAddress();
        Set<Function> functions = new LinkedHashSet<>();

        try (PrintWriter out = new PrintWriter(new FileWriter(outPath))) {
            while (cursor != null && cursor.compareTo(currentProgram.getMaxAddress()) < 0) {
                Address found = memory.findBytes(cursor, needle, null, true, monitor);
                if (found == null) break;
                out.println("String found at " + found);
                ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(found);
                while (refs.hasNext()) {
                    Reference ref = refs.next();
                    Function function = getFunctionContaining(ref.getFromAddress());
                    out.println("  ref " + ref.getFromAddress() + " -> " +
                        (function == null ? "(no function)" : function.getName() + " @ " + function.getEntryPoint()));
                    if (function != null) functions.add(function);
                }
                cursor = found.add(1);
            }

            DecompInterface decompiler = new DecompInterface();
            decompiler.openProgram(currentProgram);
            for (Function function : functions) {
                out.println();
                out.println("=== " + function.getName() + " @ " + function.getEntryPoint() + " ===");
                DecompileResults result = decompiler.decompileFunction(function, 120, monitor);
                out.println(result.decompileCompleted()
                    ? result.getDecompiledFunction().getC()
                    : "DECOMPILE FAILED: " + result.getErrorMessage());
            }
            decompiler.dispose();
        }

        println("Unsupported-modulation analysis written to " + outPath);
    }
}
