// Decompile the functions in the call chain observed live via cdb for the USB vendor control
// transfer loop (bRequest 74 SET-notify + bRequest 78 GET-status, alternating every ~2.5-3ms).
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.PrintWriter;
import java.io.FileWriter;

public class XHeadDecodeUsbLoop extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_usb_loop.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        Address base = currentProgram.getImageBase();

        long[] offs = new long[] { 0x8812fL, 0x87a0bL, 0x879b6L, 0x38ce46L, 0x7c268L, 0x7c9ebL };
        java.util.Set<Function> seen = new java.util.HashSet<>();
        for (long off : offs) {
            Address a = base.add(off);
            Function f = getFunctionContaining(a);
            if (f == null || seen.contains(f)) continue;
            seen.add(f);
            out.println("=== offset 0x" + Long.toHexString(off) + " -> function: " + f.getName() +
                " @ " + f.getEntryPoint() + " ===");
            DecompileResults res = decomp.decompileFunction(f, 180, new ConsoleTaskMonitor());
            if (res != null && res.decompileCompleted()) {
                out.println(res.getDecompiledFunction().getC());
            } else {
                out.println("Decompile failed: " + (res != null ? res.getErrorMessage() : "null"));
            }
            out.println();
        }

        decomp.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
