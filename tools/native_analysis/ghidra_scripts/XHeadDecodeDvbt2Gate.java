// 2026-07-27: follow-up to XHeadDecodeModeValidationHelper.java. DVB_T2 (Mode=7)'s branch in
// FUN_1403943c0 calls FUN_140393c20(param_1+5) as an extra gate before its own bitrate
// calculation. Decompiling this to see if it's a similar "needs an unexposed parameter" issue
// as J83A, or something else entirely (DVB_T2 has 11 declared fields, the richest of any mode,
// so there's more surface area for a genuine mismatch).
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.PrintWriter;
import java.io.FileWriter;

public class XHeadDecodeDvbt2Gate extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_dvbt2_gate.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        long[] targets = new long[] {
            0x140393c20L, // FUN_140393c20 -- DVB_T2's extra gate function
        };

        for (long targetOffset : targets) {
            Address addr = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(targetOffset);
            Function func = getFunctionContaining(addr);
            out.println("=== Target VA 0x" + Long.toHexString(targetOffset) + " ===");
            if (func == null) {
                out.println("  No function found containing this address.");
                out.println();
                continue;
            }
            out.println("Function: " + func.getName() + " @ " + func.getEntryPoint());
            DecompileResults res = decomp.decompileFunction(func, 150, new ConsoleTaskMonitor());
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
