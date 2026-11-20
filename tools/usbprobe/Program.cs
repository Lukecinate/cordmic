using System.Runtime.InteropServices;
using CordMic.Core;
using CordMic.Core.Usb;

namespace CordMic.UsbProbe;

/// <summary>
/// Read-only USB catalog probe for the 0.8.0 M1 spike. It never installs a driver and never
/// suggests turning off Secure Boot, Memory Integrity or driver signature enforcement.
/// --aoa-test only sends AOA vendor requests when a WinUSB interface is already bound
/// (route 1 precondition). Default Auto remains tethering until G-M1.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            Console.Write(Usage());
            return 0;
        }
        try
        {
            if (args[0] == "--from-report")
            {
                if (args.Length < 2) { Console.Error.WriteLine("usage: cordmic-usbprobe --from-report <file>"); return 2; }
                var report = UsbProbeReport.Parse(File.ReadAllText(args[1]));
                Console.Write(report.ToReportText());
                return 0;
            }
            if (args[0] == "--demo")
            {
                Console.Write(DemoReport().ToReportText());
                return 0;
            }
            if (args[0] == "--aoa-setups")
            {
                foreach (var s in AoaProtocol.HandshakeSetups("0.8.0-dev", "00"))
                    Console.WriteLine($"req={s.Request} type=0x{s.RequestType:X2} index={s.Index} len={s.Data.Length} timeout={s.TimeoutMs}");
                return 0;
            }
            if (args[0] == "--aoa-test")
            {
                if (args.Contains("--dry-run") || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Console.WriteLine("aoa-test=dry-run");
                    Console.WriteLine("note=WinUSB I/O runs only on Windows when an interface is already bound to WinUSB.");
                    Console.WriteLine("note=PENDING-HARDWARE: plug the phone and re-run on the PC to settle U2, U4 and U6.");
                    foreach (var s in AoaProtocol.HandshakeSetups("0.8.0-dev", AoaProtocol.NewInstallSerial()))
                        Console.WriteLine($"would-send req={s.Request} timeout={s.TimeoutMs}");
                    return 0;
                }
                Console.WriteLine("aoa-test=windows-pending");
                Console.WriteLine("note=No WinUSB open is attempted in this build until the catalog scan finds a bound WinUSB interface. PENDING-HARDWARE.");
                return 0;
            }
            if (args[0] == "--scan")
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Console.Error.WriteLine("error: SetupAPI scan is Windows-only. Use --demo or --from-report, or run this tool on the PC (PENDING-HARDWARE).");
                    return 2;
                }
                Console.WriteLine("gate=PENDING-HARDWARE");
                Console.WriteLine("note=Windows SetupAPI scan is wired in the 0.7.2 UsbProbe and will be used by a later M4 build of this tool.");
                Console.WriteLine("note=Run this command on the user's PC and send the report with hashed serials only.");
                return 0;
            }
            Console.Error.WriteLine("unknown option; try --help");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static UsbProbeReport DemoReport()
    {
        string salt = PhoneLinkHistory.NewSalt();
        var ifaces = new List<UsbInterfaceRecord>
        {
            new() { Index = 0, InterfaceClass = 0x06, InterfaceSubClass = 0x01, InterfaceProtocol = 0x01, BoundService = "WUDFWpdMtp" },
        };
        var rec = new UsbDeviceRecord
        {
            VendorId = 0x04E8,
            ProductId = 0x6860,
            HashedSerial = UsbSerialHash.Hash("DEMO-SERIAL-NOT-REAL", salt),
            Name = "SAMSUNG Mobile USB Composite Device",
            BoundService = "usbccgp",
            MsOsDescriptorUnknown = true,
        };
        rec.Interfaces = ifaces;
        rec.Kind = UsbDeviceRecord.Classify(rec.VendorId, rec.ProductId, rec.BoundService, rec.Interfaces, rec.Name);
        return new UsbProbeReport
        {
            ToolVersion = "0.8.0-dev",
            ModeNote = "demo",
            Gate = "PENDING-HARDWARE",
            SecureBoot = "PENDING-HARDWARE",
            MemoryIntegrity = "PENDING-HARDWARE",
            AutoBlocker = "PENDING-HARDWARE",
            Devices = { rec },
            Notes = { "Demo catalog only; not a measurement from the user's phone." },
        };
    }

    private static string Usage() => """
CordMic USB probe (M1). Read-only. Nothing is installed.

  cordmic-usbprobe --scan              Windows: catalog the plugged-in phone (PENDING-HARDWARE)
  cordmic-usbprobe --demo              print a fake report (CI / preview)
  cordmic-usbprobe --from-report FILE  validate and reprint a report
  cordmic-usbprobe --aoa-setups        show AOA 51/52/53 setups (no I/O)
  cordmic-usbprobe --aoa-test --dry-run

Do not enable USB debugging for CordMic. Do not turn off Secure Boot, Memory Integrity
or driver signature enforcement. Hash serials; mask IPs and Windows user names before sharing.

Exact hardware steps: docs/PENDING-HARDWARE-M1.md

""";
}
