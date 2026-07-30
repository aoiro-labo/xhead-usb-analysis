//@category XHead

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.Symbol;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.FileWriter;
import java.io.PrintWriter;
import java.util.LinkedHashMap;
import java.util.Map;

public class XHeadDecodeTransformOutputVtables extends GhidraScript {
    @Override
    public void run() throws Exception {
        String outPath =
            "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_transform_output_vtables.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));
        Memory memory = currentProgram.getMemory();
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        Map<Long, Function> functions = new LinkedHashMap<>();
        long[][] tables = {
            { 0x1404ad7d8L, 0x28 }, // mModulationDevice (read/write/block I/O methods)
            { 0x14040aba8L, 0x30 }, // mModulationUSB concrete register I/O
            { 0x14040ae18L, 0x10 }, // Channel
            { 0x14040ae28L, 0x10 }, // Program primary base
            { 0x14040ae38L, 0x58 }, // Program
            { 0x14040ae98L, 0x58 }  // Output / mTransferModer implementation
        };

        for (long[] table : tables) {
            Address base = toAddr(table[0]);
            Symbol symbol = getSymbolAt(base);
            out.println("=== " + base + " " +
                (symbol == null ? "" : symbol.getName(true)) + " ===");
            for (int offset = 0; offset < table[1]; offset += 8) {
                Address slot = base.add(offset);
                long pointer = memory.getLong(slot);
                Address target = toAddr(pointer);
                Function function = getFunctionAt(target);
                out.println(String.format("+0x%02x -> %s %s", offset, target,
                    function == null ? "" : function.getName()));
                if (function != null) functions.put(pointer, function);
            }
            out.println();
        }

        for (Function function : functions.values()) {
            out.println("=== " + function.getEntryPoint() + " " +
                function.getName() + " ===");
            DecompileResults result = decompiler.decompileFunction(
                function, 240, new ConsoleTaskMonitor());
            if (result.decompileCompleted()) {
                out.println(result.getDecompiledFunction().getC());
            } else {
                out.println("FAILED: " + result.getErrorMessage());
            }
        }
        decompiler.dispose();
        out.close();
        println("done -> " + outPath);
    }
}
