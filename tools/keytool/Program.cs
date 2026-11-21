using System.Text;
using CordMic.Core.Updates;

/// <summary>
/// Offline release-key tool. It never prints a private key or a passphrase.
/// Production keys must be generated on the maintainer's own machine, not on a shared box.
/// </summary>
internal static class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { Usage(); return 2; }
            return args[0] switch
            {
                "generate" => Generate(args),
                "check" => Check(args),
                "sign" => Sign(args),
                "verify" => Verify(args),
                "pubkey" => Pubkey(args),
                "-h" or "--help" or "help" => Help(),
                _ => Fail("unknown command " + args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    static int Help() { Usage(); return 0; }

    static void Usage()
    {
        Console.WriteLine("""
            cordmic-keytool — generate, check and use offline CordMic release keys (ECDSA P-256).
            Private keys and passphrases are never printed.

              generate --out <file.p8>
              check <file.p8>
              pubkey --key <file.p8>
              sign <release-manifest.json> --key <file.p8> [--out <release-manifest.json.sig>]
              verify <release-manifest.json> --sig <file.sig> --pub <spki-base64>
            """);
    }

    static int Generate(string[] args)
    {
        if (!Opt(args, "--out", out var path) || string.IsNullOrEmpty(path)) return Fail("generate requires --out <file.p8>");
        if (File.Exists(path)) return Fail("refusing to overwrite " + path);
        var pass = ReadPassphrase(confirm: true);
        if (!ReleaseCrypto.PassphraseStrongEnough(pass))
            return Fail("passphrase is too weak (use at least six words, or 20 characters)");
        using var key = ReleaseCrypto.CreateKey();
        var p8 = ReleaseCrypto.EncryptPkcs8(key, pass);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, p8);
        var spki = ReleaseCrypto.SpkiBase64(key);
        var id = ReleaseCrypto.KeyIdFromBase64(spki);
        Console.WriteLine("keyId=" + id);
        Console.WriteLine("spki=" + spki);
        Console.WriteLine("encrypted-pkcs8 written. Keep the passphrase off the stick that holds the file.");
        return 0;
    }

    static int Check(string[] args)
    {
        var path = Positional(args, 1) ?? OptVal(args, "--key");
        if (string.IsNullOrEmpty(path)) return Fail("check requires a .p8 path");
        var pass = ReadPassphrase(confirm: false);
        using var key = ReleaseCrypto.DecryptPkcs8(File.ReadAllBytes(path), pass);
        var spki = ReleaseCrypto.SpkiBase64(key);
        Console.WriteLine("ok keyId=" + ReleaseCrypto.KeyIdFromBase64(spki));
        return 0;
    }

    static int Pubkey(string[] args)
    {
        if (!Opt(args, "--key", out var path) || string.IsNullOrEmpty(path)) return Fail("pubkey requires --key <file.p8>");
        var pass = ReadPassphrase(confirm: false);
        using var key = ReleaseCrypto.DecryptPkcs8(File.ReadAllBytes(path), pass);
        var spki = ReleaseCrypto.SpkiBase64(key);
        Console.WriteLine("keyId=" + ReleaseCrypto.KeyIdFromBase64(spki));
        Console.WriteLine("spki=" + spki);
        return 0;
    }

    static int Sign(string[] args)
    {
        var manPath = Positional(args, 1);
        if (string.IsNullOrEmpty(manPath) || !Opt(args, "--key", out var keyPath) || string.IsNullOrEmpty(keyPath))
            return Fail("sign requires <release-manifest.json> --key <file.p8>");
        var outPath = OptVal(args, "--out") ?? manPath + ".sig";
        var man = File.ReadAllBytes(manPath);
        if (man.Length > ReleaseManifest.MaxBytes) return Fail("manifest too large");
        if (!ReleaseManifest.TryParse(man, out var data, out var err)) return Fail(err);
        Console.WriteLine("product=" + data.Product);
        Console.WriteLine("version=" + data.Version);
        Console.WriteLine("channel=" + data.Channel);
        foreach (var f in data.Files)
            Console.WriteLine($"file {f.Name} size={f.Size} sha256={f.Sha256Hex}");
        var pass = ReadPassphrase(confirm: false);
        using var key = ReleaseCrypto.DecryptPkcs8(File.ReadAllBytes(keyPath), pass);
        var id = ReleaseCrypto.KeyIdFromBase64(ReleaseCrypto.SpkiBase64(key));
        var sig = ManifestSignature.Format(id, ReleaseCrypto.SignDer(key, man));
        File.WriteAllText(outPath, sig, new UTF8Encoding(false));
        if (!ManifestSignature.TryParse(File.ReadAllBytes(outPath), out var parsed, out var perr))
            return Fail("wrote a signature that did not parse: " + perr);
        if (!ReleaseCrypto.VerifyDer(Convert.FromBase64String(ReleaseCrypto.SpkiBase64(key)), man, parsed.Der))
            return Fail("immediate verify failed");
        Console.WriteLine("signed keyId=" + id);
        Console.WriteLine("wrote " + outPath);
        return 0;
    }

    static int Verify(string[] args)
    {
        var manPath = Positional(args, 1);
        if (string.IsNullOrEmpty(manPath) || !Opt(args, "--sig", out var sigPath) || !Opt(args, "--pub", out var pub) ||
            string.IsNullOrEmpty(sigPath) || string.IsNullOrEmpty(pub))
            return Fail("verify requires <manifest> --sig <file> --pub <spki-base64>");
        var man = File.ReadAllBytes(manPath);
        var sig = File.ReadAllBytes(sigPath);
        var trust = new[] { new EmbeddedReleaseKey(ReleaseCrypto.KeyIdFromBase64(pub), pub) };
        var st = ManifestVerifier.Verify(man, sig, "0.0.0", ReleaseManifest.ChannelStable, trust, [], out var data, out var detail);
        // allow beta channel by retrying if the only issue is channel
        if (st == ManifestVerifyStatus.ChannelMismatch)
            st = ManifestVerifier.Verify(man, sig, "0.0.0", ReleaseManifest.ChannelBeta, trust, [], out data, out detail);
        Console.WriteLine(st == ManifestVerifyStatus.Ok ? "ok " + data!.Version + " " + data.Channel : "fail " + st + " " + detail);
        return st == ManifestVerifyStatus.Ok ? 0 : 1;
    }

    static string ReadPassphrase(bool confirm)
    {
        // CORDMIC_KEYTOOL_PASSPHRASE is for automated tests only. Do not use it for K1/K2.
        var env = Environment.GetEnvironmentVariable("CORDMIC_KEYTOOL_PASSPHRASE");
        if (!string.IsNullOrEmpty(env)) return env;
        Console.Write("passphrase: ");
        var a = ReadHidden();
        Console.WriteLine();
        if (!confirm) return a;
        Console.Write("again: ");
        var b = ReadHidden();
        Console.WriteLine();
        if (a != b) throw new InvalidOperationException("passphrases did not match");
        return a;
    }

    static string ReadHidden()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        return sb.ToString();
    }

    static bool Opt(string[] args, string name, out string value)
    {
        value = OptVal(args, name) ?? "";
        return value.Length > 0;
    }

    static string? OptVal(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    static string? Positional(string[] args, int i) =>
        args.Skip(1).Where(a => !a.StartsWith('-')).Skip(i - 1).FirstOrDefault();

    static int Fail(string msg)
    {
        Console.Error.WriteLine("error: " + msg);
        return 2;
    }
}
