// 2026-07-26 follow-up: a live cdb stack-trace capture (kb 6 filtered to hits where r8 is in
// 0x1280..0x1283) showed the read helper (mnservice+0x87920) is called via this chain for all
// four addresses:
//   mnservice+0x9c585 (inside FUN_14009c540, the known "adjust power" log wrapper)
//    -> function containing 0x14038ca94
//     -> function containing 0x14039bf06 (NOT FUN_14039ba70 itself -- that one ends at
//        0x14039bd7d per ghidra_rfpower_writer.txt; this is a distinct, not-yet-decompiled
//        neighbor function laid out right after it)
//      -> function containing 0x14039c067 (4 call sites ~0x99 bytes apart, one per address)
//       -> function containing 0x14038cc3f (thin wrapper)
//        -> FUN_140087920 (single-read helper)
// Decompile each of these to see how the 0x1280-0x1283 read values (0xa/0x88/0x0/0x4 observed)
// relate to PAGain, since this chain hangs off the SAME "adjust power" wrapper that also calls
// FUN_14039ba70 (the RF-power writer) -- likely a sibling/companion step of the same operation.
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.PrintWriter;
import java.io.FileWriter;
import java.util.LinkedHashSet;
import java.util.Set;

public class XHeadDecodeRfCalibrationChain extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_rf_calibration_chain.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        long[] probeAddrs = new long[] {
            0x14038ca94L,
            0x14039bf06L,
            0x14039c067L,
            0x14038cc3fL,
        };

        Set<Function> seen = new LinkedHashSet<>();
        for (long a : probeAddrs) {
            Function f = getFunctionContaining(toAddr(a));
            if (f != null) {
                seen.add(f);
            } else {
                out.println("(no function contains " + toAddr(a) + ")");
            }
        }

        for (Function f : seen) {
            out.println("=== " + f.getName() + " @ " + f.getEntryPoint() + " ===");
            DecompileResults res = decomp.decompileFunction(f, 150, new ConsoleTaskMonitor());
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
