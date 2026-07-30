//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.listing.Function;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.FileWriter;
import java.io.PrintWriter;

public class XHeadDecodeRingStartChain extends GhidraScript {
    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_ring_start_chain.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        long[] addresses = {
            0x14038d140L, 0x14038d2d0L, 0x1403965f0L, 0x14006ebf0L,
            0x14039af60L, 0x14039aa70L, 0x14039aeb0L, 0x14038e4f0L,
            0x14038d650L, 0x14038d470L, 0x14038e2e0L, 0x14039ac80L,
            0x14039a990L, 0x140398e70L,
            0x140394a70L, 0x140394ff0L, 0x140395560L, 0x1403962a0L,
            0x140394d70L, 0x140394c40L, 0x140088c20L,
            0x14038cec0L, 0x140088050L, 0x140088630L, 0x140397b80L, 0x1403924d0L, 0x140392540L,
            0x140395a30L, 0x140395af0L, 0x140391dd0L, 0x140393aa0L,
            0x140391560L, 0x140395ba0L
        };
        for (long address : addresses) {
            Function function = getFunctionAt(toAddr(address));
            out.println("=== 0x" + Long.toHexString(address) + " " +
                (function == null ? "(no function)" : function.getName()) + " ===");
            if (function != null) {
                DecompileResults result = decompiler.decompileFunction(function, 240,
                    new ConsoleTaskMonitor());
                if (result != null && result.decompileCompleted())
                    out.println(result.getDecompiledFunction().getC());
                else
                    out.println("Decompile failed: " +
                        (result == null ? "null" : result.getErrorMessage()));
            }
            out.println();
        }
        decompiler.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
