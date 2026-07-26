// 2026-07-26 follow-up: a live cdb capture showed 0x1280-0x1283 (the RF-power block whose
// *write* path is gated off, per FUN_14039ba70 in ghidra_rfpower_writer.txt) ARE actually read
// via the single-read helper at mnservice+0x87920 during ChannelStart's RF-power phase. Find the
// caller(s) of that read helper (absolute address 0x140087920) that reference 0x1280 as a literal,
// to see what the read values (0xa/0x88/0x0/0x4 observed) are used for -- possible new lead for
// PAGain's real destination (still unresolved as of ghidra_more_callers.txt).
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.PrintWriter;
import java.io.FileWriter;
import java.util.HashSet;
import java.util.Set;

public class XHeadFindRfCalibrationReader extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_rf_calibration_reader.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        // mnservice+0x87920 (single-read helper wrapping 0x4A SET + 0x4E GET), absolute address.
        Address target = toAddr(0x140087920L);
        out.println("=== Callers of single-read helper @ " + target + " ===");

        Set<Function> seen = new HashSet<>();
        ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(target);
        int count = 0;
        int totalRefs = 0;
        while (refs.hasNext()) {
            Reference ref = refs.next();
            totalRefs++;
            out.println("ref from " + ref.getFromAddress() + " type=" + ref.getReferenceType()
                + " isCall=" + ref.getReferenceType().isCall() + " isJump=" + ref.getReferenceType().isJump());
        }
        out.println("Total raw refs found: " + totalRefs);
        out.println();

        // Re-iterate for the decompile pass (iterator was consumed above).
        refs = currentProgram.getReferenceManager().getReferencesTo(target);
        while (refs.hasNext() && count < 60) {
            Reference ref = refs.next();
            Function f = getFunctionContaining(ref.getFromAddress());
            if (f != null && !seen.contains(f)) {
                seen.add(f);
                count++;
                out.println("--- Caller: " + f.getName() + " @ " + f.getEntryPoint() +
                    " (ref from " + ref.getFromAddress() + ") ---");
                DecompileResults res = decomp.decompileFunction(f, 150, new ConsoleTaskMonitor());
                if (res != null && res.decompileCompleted()) {
                    String c = res.getDecompiledFunction().getC();
                    out.println(c);
                    if (c.contains("0x1280") || c.contains("0x1281") || c.contains("0x1282") || c.contains("0x1283")) {
                        out.println(">>> REFERENCES 0x1280-0x1283 <<<");
                    }
                } else {
                    out.println("Decompile failed: " + (res != null ? res.getErrorMessage() : "null"));
                }
                out.println();
            }
        }
        out.println("Total distinct call-site callers: " + count);

        decomp.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
