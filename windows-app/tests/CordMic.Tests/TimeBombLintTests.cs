using System.Text.RegularExpressions;

namespace CordMic.Tests;

/// <summary>
/// Plan section 14: cheap static checks over CordMic.Core so intervals do not depend on
/// wall clocks and TickCount (24.9-day wrap) does not sneak back in.
/// </summary>
public class TimeBombLintTests
{
    private static string CoreRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, "src", "CordMic.Core");
            if (Directory.Exists(cand)) return cand;
            cand = Path.Combine(dir.FullName, "windows-app", "src", "CordMic.Core");
            if (Directory.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        // Running from the test output folder: ../../../../src/CordMic.Core
        var rel = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "CordMic.Core"));
        return rel;
    }

    private static IEnumerable<(string Path, string Text)> Sources()
    {
        string root = CoreRoot();
        Assert.True(Directory.Exists(root), "CordMic.Core sources not found at " + root);
        foreach (var f in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            yield return (f, File.ReadAllText(f));
        }
    }

    [Fact]
    public void Core_doesNotSubtractDateTimeNowForDurations()
    {
        var rx = new Regex(@"DateTime\.(Now|UtcNow)\s*-", RegexOptions.Compiled);
        var hits = Sources().Where(s => rx.IsMatch(s.Text)).Select(s => Path.GetFileName(s.Path)).ToList();
        Assert.True(hits.Count == 0, "DateTime.Now/UtcNow subtraction in " + string.Join(", ", hits));
    }

    [Fact]
    public void Core_doesNotUse32BitTickCount()
    {
        var rx = new Regex(@"Environment\.TickCount(?!64)", RegexOptions.Compiled);
        var hits = new List<string>();
        foreach (var (path, text) in Sources())
        {
            foreach (Match m in rx.Matches(text))
                hits.Add(Path.GetFileName(path) + ":" + LineOf(text, m.Index));
        }
        Assert.True(hits.Count == 0, "Environment.TickCount (32-bit) in " + string.Join(", ", hits));
    }

    [Fact]
    public void Transport_usesStopwatchOrTickCount64()
    {
        string usb = Sources().First(s => s.Path.EndsWith("UsbTransport.cs", StringComparison.Ordinal)).Text;
        Assert.Contains("Stopwatch.GetTimestamp", usb);
        Assert.DoesNotContain("DateTime.Now", usb);
    }

    [Fact]
    public void CatalogDocs_doNotInstructSignatureBypass()
    {
        // Only flag sentences that tell the user to do the dangerous thing.
        var rx = new Regex(@"(?i)(please |you should |run |enable |turn on )?(test-signing|bcdedit /set|disable (secure boot|memory integrity|driver signature))", RegexOptions.Compiled);
        string root = CoreRoot();
        var repo = Directory.GetParent(root)!.Parent!.Parent!.FullName; // windows-app/src/.. -> repo? src's parent is windows-app, parent is repo
        // CoreRoot is windows-app/src/CordMic.Core → repo is two up from src? Core root = .../windows-app/src/CordMic.Core, repo = ../../..
        var repoRoot = Path.GetFullPath(Path.Combine(root, "..", "..", ".."));
        string[] files =
        {
            Path.Combine(repoRoot, "docs", "PENDING-HARDWARE-M1.md"),
            Path.Combine(repoRoot, "windows-app", "driver", "cordmic-accessory.inf"),
            Path.Combine(repoRoot, "tools", "m1", "catalog-install-test.ps1"),
            Path.Combine(repoRoot, "tools", "m1", "record-windows-security.ps1"),
        };
        var hits = new List<string>();
        foreach (var f in files.Where(File.Exists))
        {
            var text = File.ReadAllText(f);
            // Allow explicit prohibitions ("never", "do not", "will not").
            foreach (var line in text.Split('\n'))
            {
                var l = line.Trim();
                if (l.StartsWith('#') || l.StartsWith(';')) continue;
                if (Regex.IsMatch(l, @"(?i)(never|do not|don't|will not|refuses|must not)")) continue;
                if (rx.IsMatch(l)) hits.Add(Path.GetFileName(f) + ": " + l);
            }
        }
        Assert.True(hits.Count == 0, string.Join("\n", hits));
    }

    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }
}
