//@category XHead
import ghidra.app.script.GhidraScript;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolIterator;
import ghidra.program.model.symbol.SymbolTable;

import java.io.PrintWriter;
import java.io.FileWriter;

public class XHeadFindSymbol extends GhidraScript {
    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_symbols.txt";
        PrintWriter out = new PrintWriter(new FileWriter(outPath));
        SymbolTable st = currentProgram.getSymbolTable();
        SymbolIterator it = st.getSymbolIterator();
        while (it.hasNext()) {
            Symbol s = it.next();
            String n = s.getName();
            if (n.contains("FailedPreconditionError") || n.contains("mnbridge") || n.equals("FUN_140027980")) {
                out.println(s.getAddress() + "  " + s.getName(true));
            }
        }
        out.close();
        println("done");
    }
}
