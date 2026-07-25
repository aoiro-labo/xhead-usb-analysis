// Decompile the function containing the call sites that wrote to 0x1220/0x1221/0x1228/0x1229/0x1290
// (observed live via cdb: RetAddr around mnservice+0x38e62f..0x38ed35).
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.PrintWriter;
import java.io.FileWriter;

public class XHeadDecodeRfPowerWriter extends GhidraScript {
    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_rfpower_writer.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        long[] offs = new long[] { 0x39bace, 0x39bb1a, 0x39bb6d, 0x39bcf8, 0x39bd35, 0x9c5c9, 0x9d635 };
        java.util.Set<Function> seen = new java.util.HashSet<>();
        for (long off : offs) {
            Address a = toAddr(0x140000000L + off);
            Function f = getFunctionContaining(a);
            out.println("=== offset 0x" + Long.toHexString(off) + " -> " +
                (f != null ? f.getName() + " @ " + f.getEntryPoint() : "NOT FOUND") + " ===");
            if (f != null && !seen.contains(f)) {
                seen.add(f);
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
