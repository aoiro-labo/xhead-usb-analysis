// 2026-07-27: follow-up to XHeadFindModeValidation.java. The "modulation param invalid" gate in
// FUN_14008d210 hinges on FUN_1403943c0(param_1+0x7f8)'s return value (uVar4) -- when that's 0,
// the function proceeds down a path that keeps the "modulation param invalid" error status;
// when non-zero, it's treated as success and a frequency-shaped calculation follows
// ((uVar4/500000)*500000-500000, suggesting frequency-in-Hz semantics). Decompiling this helper
// directly to see if it's a genuine hardware-capability/frequency-range check or a simple
// Mode-based lookup table.
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.PrintWriter;
import java.io.FileWriter;

public class XHeadDecodeModeValidationHelper extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_mode_validation_helper.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        long[] targets = new long[] {
            0x1403943c0L, // FUN_1403943c0 -- gates the "modulation param invalid" error
            0x14009b380L, // FUN_14009b380 -- called when the gate passes
            0x14009d7e0L, // FUN_14009d7e0 -- called next
            0x14009b650L, // FUN_14009b650 -- called last
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
