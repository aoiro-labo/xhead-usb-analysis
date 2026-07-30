// Dump the constant compatibility tables used by FUN_140393c20, mnservice.exe's
// DVB-T2 parameter validator. This is entirely static: it only reads the imported
// program image and never accesses the XHEAD-USB device.
//@category XHead

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.Memory;

import java.io.FileWriter;
import java.io.PrintWriter;

public class XHeadDumpDvbt2Tables extends GhidraScript {
    private static final long IMAGE_BASE = 0x140000000L;

    @Override
    public void run() throws Exception {
        String outPath = "C:\\Users\\aoiro\\Documents\\XHEAD-USB_解析\\captures\\ghidra_dvbt2_tables.txt";
        Memory memory = currentProgram.getMemory();

        try (PrintWriter out = new PrintWriter(new FileWriter(outPath))) {
            dump(out, memory, 0x1404ae0d0L, 0x80,
                "FFT x GuardInterval -> PilotPattern bit mask (validator DAT_1404ae0d0)");
            dump(out, memory, 0x1404adec8L, 0x40,
                "FEC x CodeRate -> nonzero support/value table (validator DAT_1404adec8)");
            dump(out, memory, 0x1404ae100L, 0x90,
                "DVB-T2 bitrate helper tables (DAT_1404ae100..)");
        }

        println("DVB-T2 table dump written to " + outPath);
    }

    private void dump(PrintWriter out, Memory memory, long va, int length, String label)
            throws Exception {
        Address address = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(va);
        byte[] data = new byte[length];
        memory.getBytes(address, data);

        out.printf("=== %s @ VA 0x%x (RVA 0x%x), %d bytes ===%n",
            label, va, va - IMAGE_BASE, length);
        for (int offset = 0; offset < data.length; offset += 16) {
            out.printf("%08x:", offset);
            for (int i = 0; i < 16 && offset + i < data.length; i++) {
                out.printf(" %02x", data[offset + i] & 0xff);
            }
            out.println();
        }
        out.println();
    }
}
