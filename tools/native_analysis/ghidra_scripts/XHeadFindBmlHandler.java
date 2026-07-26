// 2026-07-26: user asked about BML (data broadcasting) support at output time. Live testing found
// mnts_bml.cc:102 logs "bml file [%s] not exist." as a WARNING (channel proceeds normally without
// it) when mPSEncodeParam.BMLFile (FieldID=38, a string property pointing at a local .xbml file
// path) doesn't resolve. Placed a minimal, structurally-valid .xbml file (built by hand from the
// decompiled client-side format in decompiled/xhead_studio/xhead_usb.config/xBMLFile.cs) at the
// expected path and the warning disappeared on next launch, with no other error and normal RF
// output confirmed via RTL-SDR. This script finds the function containing that log string and
// decompiles it (plus nearby helpers) to see exactly what mnservice.exe's own native parser
// validates, to know how much confidence "no warning" really buys us.
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.listing.Data;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.PrintWriter;
import java.io.FileWriter;
import java.util.HashSet;
import java.util.Set;

public class XHeadFindBmlHandler extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_bml_handler.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        // Find the "not exist." string (part of "bml file [%s] not exist.") and walk to its
        // referencing function(s).
        String[] needles = new String[] { "not exist.", "bml file", "mnts_bml.cc" };
        Set<Function> seen = new HashSet<>();

        Memory mem = currentProgram.getMemory();
        for (String needle : needles) {
            out.println("=== Searching for string containing: \"" + needle + "\" ===");
            byte[] pattern = needle.getBytes("UTF-8");
            Address start = currentProgram.getMinAddress();
            Address found = mem.findBytes(start, pattern, null, true, monitor);
            int count = 0;
            while (found != null && count < 5) {
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
        }

        decomp.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
