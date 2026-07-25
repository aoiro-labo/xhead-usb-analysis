// Check whether mnservice.exe actually has implemented code paths for the non-ISDB_T Mode
// variants (DVB_T/J83A/ATSC/J83B/DTMB/J83C/DVB_T2), or whether the wire-protocol's multi-standard
// struct is vestigial/unimplemented for this product. Strategy: FUN_140087920 (the single-read
// handshake used for streaming notify) had param_1+0x282/0x283 as PAGain/DACGain source bytes --
// look for a Mode-dispatch table or per-mode function pointers near the modulation config code,
// and check for any string literals mentioning the OTHER standards (DVB-T, ATSC, J83, DTMB) in
// mnservice.exe, which would indicate real handling code exists for them (vs. the client-side GUI
// struct just being declared but never actually processed by the service).
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.listing.Function;

import java.io.PrintWriter;
import java.io.FileWriter;
import java.util.HashSet;
import java.util.Set;

public class XHeadCheckOtherModes extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_other_modes.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        String[] needles = new String[] {
            "DVB_T", "DVB-T", "ATSC", "J83A", "J83B", "J83C", "DTMB", "8VSB", "J.83",
            "QAM16", "QAM32", "QAM128", "QAM256"
        };

        for (String needle : needles) {
            out.println("=== Searching for: \"" + needle + "\" ===");
            Set<Address> foundAddrs = new HashSet<>();
            Memory mem = currentProgram.getMemory();
            byte[] pattern = needle.getBytes("US-ASCII");
            Address cur = currentProgram.getMinAddress();
            Address end = currentProgram.getMaxAddress();
            int hits = 0;
            while (hits < 5) {
                Address found = mem.findBytes(cur, end, pattern, null, true, monitor);
                if (found == null) break;
                hits++;
                foundAddrs.add(found);
                out.println("  found at " + found);
                Set<Function> seen = new HashSet<>();
                ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(found);
                while (refs.hasNext()) {
                    Reference ref = refs.next();
                    Function f = getFunctionContaining(ref.getFromAddress());
                    if (f != null && !seen.contains(f)) {
                        seen.add(f);
                        out.println("    referenced from function: " + f.getName() + " @ " + f.getEntryPoint());
                    }
                }
                cur = found.add(pattern.length);
            }
            if (hits == 0) {
                out.println("  (not found anywhere in the binary)");
            }
            out.println();
        }

        out.close();
        println("done -> " + outPath);
    }
}
