// 2026-07-27: J83A/DVB_T2's ChannelStart is rejected cleanly with ErrMessage="modulation param
// invalid" (docs/protocol/modulation_capabilities.md 続報13), unlike DTMB/J83C which are accepted
// by validation but hang later inside mnservice.exe's own hardware-handshake code (続報22 showed
// DTMB/J83C actually work fine via direct_usb, bypassing that hang). The open question: is J83A/
// DVB_T2's rejection a genuine hardware-capability check (this silicon/SKU truly can't do these
// standards), or an arbitrary software-side whitelist that direct_usb's raw register writes could
// also bypass like DTMB/J83C? This script finds the "modulation param invalid" string and
// decompiles whatever function(s) reference it, to see what exactly triggers the rejection --
// purely static, no live device access, zero hardware risk.
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.mem.Memory;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.PrintWriter;
import java.io.FileWriter;
import java.util.HashSet;
import java.util.Set;

public class XHeadFindModeValidation extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_mode_validation.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        String[] needles = new String[] { "modulation param invalid", "param invalid" };
        Set<Function> seen = new HashSet<>();

        Memory mem = currentProgram.getMemory();
        for (String needle : needles) {
            out.println("=== Searching for string containing: \"" + needle + "\" ===");
            byte[] pattern = needle.getBytes("UTF-8");
            Address start = currentProgram.getMinAddress();
            Address found = mem.findBytes(start, pattern, null, true, monitor);
            int count = 0;
            while (found != null && count < 10) {
                out.println("  found @ " + found);
                ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(found);
                while (refs.hasNext()) {
                    Reference ref = refs.next();
                    Function f = getFunctionContaining(ref.getFromAddress());
                    if (f != null && !seen.contains(f)) {
                        seen.add(f);
                        out.println("  --> referenced by function " + f.getName() + " @ " + f.getEntryPoint());
                    }
                }
                Address next = found.add(1);
                if (next.compareTo(currentProgram.getMaxAddress()) >= 0) break;
                found = mem.findBytes(next, pattern, null, true, monitor);
                count++;
            }
            out.println();
        }

        out.println("=== Decompiling all referencing functions found above ===");
        for (Function f : seen) {
            out.println("--- " + f.getName() + " @ " + f.getEntryPoint() + " ---");
            DecompileResults res = decomp.decompileFunction(f, 150, new ConsoleTaskMonitor());
            if (res != null && res.decompileCompleted()) {
                out.println(res.getDecompiledFunction().getC());
            } else {
                out.println("Decompile failed: " + (res != null ? res.getErrorMessage() : "null"));
            }
            out.println();

            out.println("--- Callers of " + f.getName() + " (up to 5) ---");
            ReferenceIterator callerRefs = currentProgram.getReferenceManager().getReferencesTo(f.getEntryPoint());
            int callerCount = 0;
            Set<String> seenCallers = new HashSet<>();
            while (callerRefs.hasNext() && callerCount < 5) {
                Reference ref = callerRefs.next();
                Function caller = getFunctionContaining(ref.getFromAddress());
                if (caller == null) continue;
                String key = caller.getEntryPoint().toString();
                if (seenCallers.contains(key)) continue;
                seenCallers.add(key);
                callerCount++;
                out.println("Caller: " + caller.getName() + " @ " + caller.getEntryPoint());
                DecompileResults cres = decomp.decompileFunction(caller, 150, new ConsoleTaskMonitor());
                if (cres != null && cres.decompileCompleted()) {
                    out.println(cres.getDecompiledFunction().getC());
                } else {
                    out.println("  (caller decompile failed)");
                }
                out.println();
            }
        }

        decomp.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
