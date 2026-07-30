//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.symbol.Symbol;

import java.io.FileWriter;
import java.io.PrintWriter;

public class XHeadFindTransferChannelVtable extends GhidraScript {
    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_transfer_channel_vtable.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));
        Memory memory = currentProgram.getMemory();
        long[] targets = { 0x140096280L, 0x140095720L };
        for (long value : targets) {
            Address target = toAddr(value);
            out.println("=== refs to " + target + " ===");
            ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(target);
            while (refs.hasNext()) {
                Reference reference = refs.next();
                Address from = reference.getFromAddress();
                Symbol symbol = getSymbolAt(from);
                out.println("ref=" + from + " type=" + reference.getReferenceType() +
                    " symbol=" + (symbol == null ? "" : symbol.getName(true)));
                long aligned = from.getOffset() & ~7L;
                for (int delta = -0x100; delta <= 0x140; delta += 8) {
                    Address slot = toAddr(aligned + delta);
                    try {
                        long pointer = memory.getLong(slot);
                        Function function = getFunctionAt(toAddr(pointer));
                        Symbol slotSymbol = getSymbolAt(slot);
                        out.println("  " + slot + " -> " + toAddr(pointer) + " " +
                            (function == null ? "" : function.getName()) + " " +
                            (slotSymbol == null ? "" : slotSymbol.getName(true)));
                    } catch (Exception ignored) {}
                }
            }
        }
        out.close();
        println("done -> " + outPath);
    }
}
