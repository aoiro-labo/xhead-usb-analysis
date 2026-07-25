// Decompile the exact call chain captured live via cdb for the ProgramApply "bad status"
// failure: mnservice+0x9a511 -> 0x8c5c7 -> 0x96ddf -> 0x28f3a (then generic dispatch tail).
// Base assumed 0x140000000 (Ghidra's default PE image base for this import).
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.PrintWriter;
import java.io.FileWriter;

public class XHeadProgramApplyStack extends GhidraScript {

    private long[] offsets = new long[] {
        0x9a511L, 0x8c5c7L, 0x96ddfL, 0x28f3aL, 0x23249L, 0x21ce9L
    };

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_programapply_stack.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        Address base = currentProgram.getImageBase();
        out.println("Image base: " + base);

        for (long off : offsets) {
            Address a = base.add(off);
            Function f = getFunctionContaining(a);
            out.println("=== offset 0x" + Long.toHexString(off) + " -> addr " + a + " -> function: " +
                (f != null ? f.getName() + " @ " + f.getEntryPoint() : "NOT FOUND") + " ===");
            if (f != null) {
                DecompileResults res = decomp.decompileFunction(f, 180, new ConsoleTaskMonitor());
                if (res != null && res.decompileCompleted()) {
                    out.println(res.getDecompiledFunction().getC());
                } else {
                    out.println("Decompile failed: " + (res != null ? res.getErrorMessage() : "null"));
                }
            }
            out.println();
        }

        decomp.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
