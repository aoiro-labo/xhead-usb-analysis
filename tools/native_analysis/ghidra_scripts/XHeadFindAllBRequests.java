// Find every caller of FUN_140088050 (the per-request WinUsb_ControlTransfer helper that takes
// a literal bRequest constant as its 3rd argument) to enumerate the FULL vendor command set used
// anywhere in mnservice.exe, not just what was observed live via one test run.
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

public class XHeadFindAllBRequests extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_all_brequests.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        Address target = toAddr(0x140088050L);
        out.println("=== All callers of FUN_140088050 (per-request ControlTransfer helper) ===");

        Set<Function> seen = new HashSet<>();
        ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(target);
        int count = 0;
        while (refs.hasNext() && count < 40) {
            Reference ref = refs.next();
            Function f = getFunctionContaining(ref.getFromAddress());
            if (f != null && !seen.contains(f)) {
                seen.add(f);
                count++;
                out.println("--- Caller: " + f.getName() + " @ " + f.getEntryPoint() +
                    " (ref from " + ref.getFromAddress() + ") ---");
                DecompileResults res = decomp.decompileFunction(f, 120, new ConsoleTaskMonitor());
                if (res != null && res.decompileCompleted()) {
                    out.println(res.getDecompiledFunction().getC());
                } else {
                    out.println("Decompile failed: " + (res != null ? res.getErrorMessage() : "null"));
                }
                out.println();
            }
        }
        out.println("Total distinct callers: " + count);

        decomp.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
