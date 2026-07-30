//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolIterator;

import java.io.FileWriter;
import java.io.PrintWriter;

public class XHeadListVftables extends GhidraScript {
    @Override
    public void run() throws Exception {
        String outPath =
            "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_all_vftables.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));
        SymbolIterator symbols = currentProgram.getSymbolTable().getAllSymbols(true);
        while (symbols.hasNext()) {
            Symbol symbol = symbols.next();
            String name = symbol.getName(true);
            String lower = name.toLowerCase();
            if (lower.contains("vftable") && !lower.contains("meta_ptr")) {
                out.println(symbol.getAddress() + " " + name);
            }
        }
        out.close();
        println("done -> " + outPath);
    }
}
