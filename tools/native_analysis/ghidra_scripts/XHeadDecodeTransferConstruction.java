//@category XHead

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.FileWriter;
import java.io.PrintWriter;
import java.util.LinkedHashSet;
import java.util.Set;

public class XHeadDecodeTransferConstruction extends GhidraScript {
    @Override
    public void run() throws Exception {
        String outPath =
            "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_transfer_construction.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        Set<Function> functions = new LinkedHashSet<>();
        long[] seeds = {
            0x140094f60L, 0x140095250L, 0x1400952d0L, 0x140095310L,
            0x1400953a0L, 0x140095690L, 0x140095870L, 0x140096030L,
            0x140096280L, 0x140096630L, 0x140096700L
        };
        for (long seed : seeds) {
            Function function = getFunctionContaining(toAddr(seed));
            if (function != null) functions.add(function);
        }

        // Constructors/factories that reference the abstract vtable are especially useful.
        Address baseVtable = toAddr(0x14040c2c8L);
        ReferenceIterator refs =
            currentProgram.getReferenceManager().getReferencesTo(baseVtable);
        while (refs.hasNext()) {
            Reference reference = refs.next();
            Function function = getFunctionContaining(reference.getFromAddress());
            if (function != null) functions.add(function);
        }

        for (Function function : functions) {
            out.println();
            out.println("=== " + function.getEntryPoint() + " " +
                function.getName() + " ===");
            DecompileResults result = decompiler.decompileFunction(
                function, 240, new ConsoleTaskMonitor());
            if (result.decompileCompleted()) {
                out.println(result.getDecompiledFunction().getC());
            } else {
                out.println("FAILED: " + result.getErrorMessage());
            }
        }
        decompiler.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
