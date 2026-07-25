// Decompile the function containing the CmdProgramApply "bad status" precondition check
// (found via capstone/pefile static analysis: cmp dword ptr [rbp+0x58],3 ; jne bad_status
// at file VA 0x14002580b, function starts at 0x1400257b0), plus decompile each of its
// callers so we can see what concrete type/struct is being passed as the first argument
// (the one whose +0x58 field gets checked against msStatus.StatusReady).
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

public class XHeadAnalyze extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_analysis.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        long[] targets = new long[] {
            0x1400257b0L,  // function containing the [rbp+0x58]==3 check
            0x140025921L,  // (address of the bad-status LEA itself, in case function start guess is off)
        };

        for (long targetOffset : targets) {
            Address addr = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(targetOffset);
            Function func = getFunctionContaining(addr);
            out.println("=== Target VA 0x" + Long.toHexString(targetOffset) + " ===");
            if (func == null) {
                out.println("  No function found containing this address.");
                continue;
            }
            out.println("Function: " + func.getName() + " @ " + func.getEntryPoint());
            out.println("Signature: " + func.getSignature());
            out.println();

            DecompileResults res = decomp.decompileFunction(func, 120, new ConsoleTaskMonitor());
            if (res != null && res.decompileCompleted()) {
                out.println("--- Decompiled ---");
                out.println(res.getDecompiledFunction().getC());
            } else {
                out.println("Decompile failed: " + (res != null ? res.getErrorMessage() : "null result"));
            }
            out.println();

            out.println("--- Callers of " + func.getName() + " ---");
            Address entry = func.getEntryPoint();
            ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(entry);
            int callerCount = 0;
            while (refs.hasNext() && callerCount < 5) {
                Reference ref = refs.next();
                Function callerFunc = getFunctionContaining(ref.getFromAddress());
                if (callerFunc == null) continue;
                callerCount++;
                out.println("Caller: " + callerFunc.getName() + " @ " + callerFunc.getEntryPoint() + " (call site " + ref.getFromAddress() + ")");
                DecompileResults cres = decomp.decompileFunction(callerFunc, 120, new ConsoleTaskMonitor());
                if (cres != null && cres.decompileCompleted()) {
                    out.println(cres.getDecompiledFunction().getC());
                } else {
                    out.println("  (caller decompile failed)");
                }
                out.println();
            }
            out.println("=====================================");
            out.println();
        }

        decomp.dispose();
        out.close();
        println("Wrote analysis to " + outPath);
    }
}
