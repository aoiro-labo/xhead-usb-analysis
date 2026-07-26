// 2026-07-26: `strings` on mnservice.exe found real native classes beyond the exposed
// mEPGSimpleParam: mazo::mbroadcast::mEPGTable (with nested Event/Segment types!),
// mazo::mrevolution::mEPGSchedule, mEPGPresentFollowing, mEPGSimple::Coordinator, and
// mazo::mrevolution::METSITableEIT (the actual EIT table generator). No other exposed property
// group name (e.g. "mEPGParam") exists as a string, so mEPGSimpleParam is the only client-facing
// input -- but the underlying classes suggest a more capable internal representation. Find and
// decompile mEPGSimple::Coordinator's methods (the likely single-event -> schedule transformer)
// to see whether the 1-event limit is baked in even internally, or just an artifact of what the
// exposed property surface allows.
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.PrintWriter;
import java.io.FileWriter;

public class XHeadFindEpgCoordinator extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_epg_coordinator.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        // Find all functions whose mangled/demangled name mentions Coordinator, mEPGSchedule,
        // mEPGTable, or METSITableEIT -- Ghidra should have demangled at least some of these
        // given the RTTI-derived symbols found via `strings`.
        String[] needles = { "Coordinator", "mEPGSchedule", "mEPGTable", "METSITableEIT", "mEPGSimple" };
        FunctionIterator allFuncs = currentProgram.getFunctionManager().getFunctions(true);
        int count = 0;
        while (allFuncs.hasNext() && count < 40) {
            Function f = allFuncs.next();
            String name = f.getName();
            for (String needle : needles) {
                if (name.contains(needle)) {
                    out.println("--- " + name + " @ " + f.getEntryPoint() + " ---");
                    count++;
                    break;
                }
            }
        }
        out.println("Total matching functions found by name: " + count);
        out.println();

        decomp.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
