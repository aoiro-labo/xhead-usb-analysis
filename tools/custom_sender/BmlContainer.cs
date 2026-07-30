using System;
using System.IO;

namespace XHeadSender
{
    /// <summary>
    /// XHEAD-STUDIOのxBMLFile.csから復元した独自XBMLコンテナの最小writer。
    /// 入力は単一PIDだけを含む188-byte TS。公式実装と同じくComponent Tag 0x40/0x60のみ許可する。
    /// </summary>
    internal static class BmlContainer
    {
        private const uint HeaderTag = 4201644322u;
        private const uint StreamTag = 4221112873u;
        private const uint EndTag = 4235331587u;
        private const int PacketSize = 188;

        public static ushort Create(string inputTs, string outputXbml, byte componentTag, uint bitrate)
        {
            if (componentTag != 0x40 && componentTag != 0x60)
                throw new ArgumentOutOfRangeException(nameof(componentTag), "公式実装が受理する値は0x40または0x60です。");
            byte[] raw = File.ReadAllBytes(inputTs);
            ushort pid = ValidateSinglePidTs(raw);
            byte[] esInfo = CreateEsInfo(componentTag, out uint esInfoLength);
            uint totalLength = checked((uint)(16 + 84 + raw.Length));

            using (var writer = new BinaryWriter(new FileStream(outputXbml, FileMode.Create, FileAccess.Write)))
            {
                writer.Write(HeaderTag);
                writer.Write(totalLength);
                writer.Write(1u);
                writer.Write(StreamTag);
                writer.Write(pid);
                writer.Write(componentTag);
                writer.Write((byte)0);
                writer.Write(bitrate);
                writer.Write(esInfoLength);
                writer.Write((uint)raw.Length);
                writer.Write(esInfo);
                writer.Write(raw);
                writer.Write(EndTag);
            }
            return pid;
        }

        private static ushort ValidateSinglePidTs(byte[] raw)
        {
            if (raw.Length == 0 || raw.Length % PacketSize != 0)
                throw new InvalidDataException("入力は空でない188-byte TSである必要があります。");
            ushort? pid = null;
            for (int offset = 0; offset < raw.Length; offset += PacketSize)
            {
                if (raw[offset] != 0x47)
                    throw new InvalidDataException($"TS同期バイトがありません: offset={offset}");
                ushort current = (ushort)(((raw[offset + 1] & 0x1F) << 8) | raw[offset + 2]);
                if (current == 0) throw new InvalidDataException("PAT (PID 0x0000)はXBML素材として受理されません。");
                if (!pid.HasValue) pid = current;
                else if (pid.Value != current)
                    throw new InvalidDataException($"複数PIDが含まれています: 0x{pid.Value:X4}, 0x{current:X4}");
            }
            return pid.Value;
        }

        private static byte[] CreateEsInfo(byte tag, out uint length)
        {
            byte[] result = new byte[64];
            result[0] = 0x52;
            result[1] = 0x01;
            result[2] = tag;
            result[3] = 0xFD;
            result[5] = 0x00;
            result[6] = 0x0C;
            byte[] tail = tag == 0x40
                ? new byte[] { 0x33, 0x3F, 0x00, 0x03, 0x00, 0x00, 0xFF, 0xBF }
                : new byte[] { 0x1F, 0xFF, 0xBF };
            result[4] = (byte)(2 + tail.Length);
            Buffer.BlockCopy(tail, 0, result, 7, tail.Length);
            length = (uint)(7 + tail.Length);
            return result;
        }
    }
}
