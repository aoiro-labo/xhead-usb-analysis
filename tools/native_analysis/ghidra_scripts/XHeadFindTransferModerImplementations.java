//@category XHead

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolIterator;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.FileWriter;
import java.io.PrintWriter;
import java.util.HashSet;
import java.util.Set;

public class XHeadFindTransferModerImplementations extends GhidraScript {
    @Override
    public void run() throws Exception {
        String outPath =
            "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_transfer_moder_implementations.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));
        Memory memory = currentProgram.getMemory();
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        Set<Long> decoded = new HashSet<>();

        SymbolIterator symbols = currentProgram.getSymbolTable().getAllSymbols(true);
        while (symbols.hasNext()) {
            Symbol symbol = symbols.next();
            String qualified = symbol.getName(true);
            String lower = qualified.toLowerCase();
            if (!lower.contains("transfermoder") &&
                !lower.contains("transfermod") &&
                !lower.contains("mtransformoutput::output") &&
                !lower.contains("xhead")) {
                continue;
            }

            Address base = symbol.getAddress();
            out.println();
            out.println("=== " + qualified + " @ " + base + " type=" +
                symbol.getSymbolType() + " ===");

            if (lower.contains("vftable") || lower.contains("vtable")) {
                for (int offset = 0; offset <= 0x70; offset += 8) {
                    Address slot = base.add(offset);
                    try {
                        long pointer = memory.getLong(slot);
                        Address target = toAddr(pointer);
                        Function function = getFunctionAt(target);
                        Symbol targetSymbol = getSymbolAt(target);
                        out.println(String.format("+0x%02x %s -> %s function=%s symbol=%s",
                            offset, slot, target,
                            function == null ? "" : function.getName(),
                            targetSymbol == null ? "" : targetSymbol.getName(true)));

                        // +0x40 builds the optional program block passed to mbc_transform.
                        if (offset == 0x40 && function != null && decoded.add(pointer)) {
                            DecompileResults result = decompiler.decompileFunction(
                                function, 240, new ConsoleTaskMonitor());
                            out.println("--- decompile vtable +0x40 ---");
                            if (result.decompileCompleted()) {
                                out.println(result.getDecompiledFunction().getC());
                            } else {
                                out.println("FAILED: " + result.getErrorMessage());
                            }
                        }
                    } catch (Exception e) {
                        out.println(String.format("+0x%02x unreadable: %s",
                            offset, e.getMessage()));
                    }
                }

                out.println("--- references to vtable ---");
                ReferenceIterator refs =
                    currentProgram.getReferenceManager().getReferencesTo(base);
                while (refs.hasNext()) {
                    Reference reference = refs.next();
                    Function caller = getFunctionContaining(reference.getFromAddress());
                    out.println(reference.getFromAddress() + " " +
                        reference.getReferenceType() + " caller=" +
                        (caller == null ? "" : caller.getName()));
                }
            }
        }

        decompiler.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
