using System.Text;

namespace RS8Control.Hid;

/// <summary>HID 报告描述符解析结果（只取我们关心的部分：顶层集合与报告长度）。</summary>
public sealed class HidReportDescriptorInfo
{
    public sealed class Collection
    {
        public int Depth { get; set; }
        public bool IsApplication { get; set; }
        public ushort UsagePage { get; set; }
        public ushort Usage { get; set; }
        public int ReportId { get; set; }
        public int InputBytes { get; set; }
        public int OutputBytes { get; set; }
        public int FeatureBytes { get; set; }
        internal int InputBits { get; set; }
        internal int OutputBits { get; set; }
        internal int FeatureBits { get; set; }
        public List<string> Usages { get; } = new();

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"Collection usagePage=0x{UsagePage:X4} usage=0x{Usage:X4}");
            if (ReportId != 0) sb.Append($" reportId={ReportId}");
            sb.Append($" IN={InputBytes}B OUT={OutputBytes}B FEAT={FeatureBytes}B");
            return sb.ToString();
        }
    }

    public List<Collection> Collections { get; } = new();
    public List<string> Notes { get; } = new();
    public int RawLength { get; set; }

    public static HidReportDescriptorInfo Parse(byte[] d)
    {
        var info = new HidReportDescriptorInfo { RawLength = d.Length };

        ushort usagePage = 0;
        ushort reportSize = 0;
        uint reportCount = 0;
        int reportId = 0;
        var usageStack = new Stack<ushort>();
        ushort lastUsage = 0;
        Collection? current = null;
        var stack = new Stack<Collection?>();

        int i = 0;
        while (i < d.Length)
        {
            byte b = d[i];
            if (b == 0xFE)
            {
                if (i + 1 >= d.Length) break;
                int dataSize = d[i + 1];
                i += 3 + dataSize;
                continue;
            }
            int size = b & 0x03;
            if (size == 3) size = 4;
            int type = (b >> 2) & 0x03;
            int tag = (b >> 4) & 0x0F;

            uint data = 0;
            for (int k = 0; k < size && i + 1 + k < d.Length; k++)
                data |= (uint)d[i + 1 + k] << (8 * k);

            switch (type)
            {
                case 0: // Main
                    switch (tag)
                    {
                        case 0x8: // Input
                            if (current != null) current.InputBits += (int)(reportCount * reportSize);
                            break;
                        case 0x9: // Output
                            if (current != null) current.OutputBits += (int)(reportCount * reportSize);
                            break;
                        case 0xB: // Feature
                            if (current != null) current.FeatureBits += (int)(reportCount * reportSize);
                            break;
                        case 0xA: // Collection
                            var col = new Collection
                            {
                                Depth = stack.Count,
                                IsApplication = data == 0x01,
                                UsagePage = usagePage,
                                Usage = usageStack.Count > 0 ? usageStack.Pop() : lastUsage,
                                ReportId = reportId,
                            };
                            info.Collections.Add(col);
                            stack.Push(current);
                            current = col;
                            break;
                        case 0xC: // End Collection
                            current = stack.Count > 0 ? stack.Pop() : null;
                            break;
                    }
                    if (tag is 0x8 or 0x9 or 0xB or 0xA)
                        usageStack.Clear();
                    break;

                case 1: // Global
                    switch (tag)
                    {
                        case 0x0: usagePage = (ushort)data; break;
                        case 0x7: reportSize = (ushort)data; break;
                        case 0x9: reportCount = data; break;
                        case 0x8: reportId = (int)data; break;
                    }
                    break;

                case 2: // Local
                    if (tag == 0x0) { lastUsage = (ushort)data; usageStack.Push((ushort)data); }
                    else if (tag == 0x1) { lastUsage = (ushort)data; usageStack.Push((ushort)data); }
                    else if (tag == 0x2) { usageStack.Push((ushort)data); }
                    break;
            }

            i += 1 + size;
        }

        foreach (var col in info.Collections)
        {
            col.InputBytes = (col.InputBits + 7) / 8;
            col.OutputBytes = (col.OutputBits + 7) / 8;
            col.FeatureBytes = (col.FeatureBits + 7) / 8;
        }

        return info;
    }

    public static string HexDump(byte[] d, int perLine = 16)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < d.Length; i += perLine)
        {
            sb.Append($"    {i:X4}  ");
            for (int k = 0; k < perLine; k++)
                sb.Append(i + k < d.Length ? $"{d[i + k]:X2} " : "   ");
            sb.Append(' ');
            for (int k = 0; k < perLine && i + k < d.Length; k++)
            {
                byte c = d[i + k];
                sb.Append(c is >= 0x20 and < 0x7F ? (char)c : '.');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
