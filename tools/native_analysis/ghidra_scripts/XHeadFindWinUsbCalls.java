// Find calls to WinUsb_ControlTransfer / WinUsb_WritePipe / WinUsb_ReadPipe in mnservice.exe and
// decompile their callers, to understand the vendor control-transfer protocol (bRequest 74/78/79)
// observed live via USBPcap: 0x4A (host->device, wIndex=buffer info?), 0x4E (device->host, 8-byte
// status), 0x4F (seen near stream stop).
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.symbol.SymbolIterator;
import ghidra.program.model.symbol.SymbolTable;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.PrintWriter;
import java.io.FileWriter;
import java.util.HashSet;
import java.util.Set;

public class XHeadFindWinUsbCalls extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_winusb_calls.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        // Literal thunk addresses discovered in an earlier pass (getReferencesTo the EXTERNAL
        // symbol found these) -- search callers of these addresses directly, since name-based
        // lookup (getGlobalFunctions) doesn't seem to resolve these auto-named import thunks.
        String[] names = {
            "WinUsb_ControlTransfer", "WinUsb_WritePipe", "WinUsb_ReadPipe",
            "WinUsb_Initialize", "WinUsb_QueryInterfaceSettings"
        };
        long[] thunkOffsets = {
            0x1403894a4L, 0x14038949eL, 0x140389498L, 0x140389474L, 0x140389486L
        };

        for (int idx = 0; idx < names.length; idx++) {
            Address thunkAddr = toAddr(thunkOffsets[idx]);
            out.println("=== " + names[idx] + " (thunk @ " + thunkAddr + ") ===");
            Function thunkFn = getFunctionAt(thunkAddr);

            Set<Function> seen = new HashSet<>();
            ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(thunkAddr);
            int count = 0;
            while (refs.hasNext() && count < 10) {
                Reference ref = refs.next();
                Function f = getFunctionContaining(ref.getFromAddress());
                if (f != null && (thunkFn == null || !f.equals(thunkFn)) && !seen.contains(f)) {
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
            if (count == 0) {
                out.println("(no external callers found)");
            }
            out.println();
        }

        decomp.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
