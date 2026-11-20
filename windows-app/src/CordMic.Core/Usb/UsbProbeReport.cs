using System.Globalization;
using System.Text;

namespace CordMic.Core.Usb;

/// <summary>
/// Text report written by <c>cordmic-usbprobe</c>. Serials are hashed; IPs and user names are refused.
/// </summary>
public sealed class UsbProbeReport
{
    public string ToolVersion { get; set; } = "";
    public string ModeNote { get; set; } = "";
    public bool AoaTestRequested { get; set; }
    public string? SecureBoot { get; set; }
    public string? MemoryIntegrity { get; set; }
    public string? AutoBlocker { get; set; }
    public string Gate { get; set; } = "PENDING-HARDWARE";
    public List<UsbDeviceRecord> Devices { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    public string ToReportText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# CordMic USB probe report");
        sb.AppendLine("# Serial numbers are salted hashes only. Do not add IP addresses or Windows user names.");
        sb.AppendLine("gate=" + Gate);
        if (!string.IsNullOrEmpty(ToolVersion)) sb.AppendLine("tool=" + ToolVersion);
        if (!string.IsNullOrEmpty(ModeNote)) sb.AppendLine("usb_mode=" + SanitizeLine(ModeNote));
        sb.AppendLine("aoa_test=" + (AoaTestRequested ? "requested" : "not-requested"));
        sb.AppendLine("secure_boot=" + (SecureBoot ?? "PENDING-HARDWARE"));
        sb.AppendLine("memory_integrity=" + (MemoryIntegrity ?? "PENDING-HARDWARE"));
        sb.AppendLine("auto_blocker=" + (AutoBlocker ?? "PENDING-HARDWARE"));
        sb.AppendLine("devices=" + Devices.Count);
        foreach (var d in Devices)
        {
            sb.AppendLine("device");
            sb.AppendLine("  vid=" + d.VendorId.ToString("X4", CultureInfo.InvariantCulture));
            sb.AppendLine("  pid=" + d.ProductId.ToString("X4", CultureInfo.InvariantCulture));
            sb.AppendLine("  hashed_serial=" + d.HashedSerial);
            sb.AppendLine("  name=" + SanitizeLine(d.Name));
            sb.AppendLine("  bound=" + SanitizeLine(d.BoundService));
            sb.AppendLine("  kind=" + d.Kind);
            sb.AppendLine("  problem=" + (d.ProblemCode is int c ? c.ToString(CultureInfo.InvariantCulture) : "none"));
            string msos = d.MsOsDescriptorUnknown ? "unknown" : (d.MsOsDescriptorPresent ? "yes" : "no");
            sb.AppendLine("  msos=" + msos);
            foreach (var i in d.Interfaces)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  if={0} class={1:X2}/{2:X2}/{3:X2} service={4} winusb={5}",
                    i.Index, i.InterfaceClass, i.InterfaceSubClass, i.InterfaceProtocol,
                    SanitizeLine(i.BoundService), i.MsOsCompatibleWinUsb ? "yes" : "no"));
            }
        }
        foreach (var n in Notes)
            sb.AppendLine("note=" + SanitizeLine(n));
        return sb.ToString();
    }

    public static UsbProbeReport Parse(string text)
    {
        var r = new UsbProbeReport();
        UsbDeviceRecord? cur = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line == "device") { cur = new UsbDeviceRecord(); r.Devices.Add(cur); continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();
            if (key.StartsWith("  ")) key = key.Trim();
            bool indented = raw.StartsWith("  ") || raw.StartsWith('\t');
            if (!indented)
            {
                switch (key)
                {
                    case "gate": r.Gate = val; break;
                    case "tool": r.ToolVersion = val; break;
                    case "usb_mode": r.ModeNote = val; break;
                    case "aoa_test": r.AoaTestRequested = val == "requested"; break;
                    case "secure_boot": r.SecureBoot = val; break;
                    case "memory_integrity": r.MemoryIntegrity = val; break;
                    case "auto_blocker": r.AutoBlocker = val; break;
                    case "note": r.Notes.Add(val); break;
                }
                continue;
            }
            if (cur is null) continue;
            switch (key)
            {
                case "vid": cur.VendorId = ParseHex(val); break;
                case "pid": cur.ProductId = ParseHex(val); break;
                case "hashed_serial":
                    if (UsbSerialHash.LooksLikeRawSerial(val)) throw new FormatException("report must not contain a raw serial");
                    cur.HashedSerial = val;
                    break;
                case "name": cur.Name = val; break;
                case "bound": cur.BoundService = val; break;
                case "kind":
                    cur.Kind = Enum.TryParse(val, out UsbFunctionKind k) ? k : UsbFunctionKind.Unknown;
                    break;
                case "problem":
                    cur.ProblemCode = val == "none" ? null : int.Parse(val, CultureInfo.InvariantCulture);
                    break;
                case "msos":
                    cur.MsOsDescriptorUnknown = val == "unknown";
                    cur.MsOsDescriptorPresent = val == "yes";
                    break;
                case "if":
                    cur.Interfaces.Add(ParseIface(val));
                    break;
            }
        }
        return r;
    }

    /// <summary>Refuses text that looks like an IPv4/IPv6 address or a Windows user path.</summary>
    public static string SanitizeLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (LooksLikeAddressOrUser(s)) return "[redacted]";
        return s.Replace('\n', ' ').Replace('\r', ' ');
    }

    public static bool LooksLikeAddressOrUser(string s)
    {
        if (s.Contains("C:\\Users\\", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Contains("/Users/", StringComparison.OrdinalIgnoreCase)) return true;
        // dotted IPv4
        int dots = 0;
        bool digitRun = false;
        int groups = 0;
        foreach (char c in s)
        {
            if (c is >= '0' and <= '9') { if (!digitRun) { groups++; digitRun = true; } }
            else if (c == '.') { dots++; digitRun = false; }
            else { dots = 0; groups = 0; digitRun = false; }
            if (dots == 3 && groups == 4) return true;
        }
        if (s.Contains(':') && s.Count(c => c == ':') >= 2 && s.Any(char.IsAsciiHexDigit))
        {
            // likely IPv6
            if (s.Split('%', 2)[0].Count(c => c == ':') >= 2) return true;
        }
        return false;
    }

    private static int ParseHex(string val) =>
        int.Parse(val, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static UsbInterfaceRecord ParseIface(string val)
    {
        // class=08/06/50 service=USBSTOR winusb=no  — plus leading "0 class=..."
        var i = new UsbInterfaceRecord();
        foreach (var part in val.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 0 && int.TryParse(part, out int idx)) { i.Index = idx; continue; }
            if (eq <= 0) continue;
            string k = part[..eq];
            string v = part[(eq + 1)..];
            if (k == "class")
            {
                var bits = v.Split('/');
                if (bits.Length >= 3)
                {
                    i.InterfaceClass = ParseHex(bits[0]);
                    i.InterfaceSubClass = ParseHex(bits[1]);
                    i.InterfaceProtocol = ParseHex(bits[2]);
                }
            }
            else if (k == "service") i.BoundService = v;
            else if (k == "winusb") i.MsOsCompatibleWinUsb = v == "yes";
        }
        return i;
    }
}
