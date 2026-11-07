namespace CordMic.Core;

/// <summary>
/// The Windows helper accepts exactly one fixed verb. No paths, device ids,
/// format numbers or extra tokens are taken from the command line.
/// </summary>
public static class CableHelperArgs
{
    public const string Configure = "--configure-cable";
    public const string Restore = "--restore-cable";

    public enum Verb { None, Configure, Restore }

    public const int ExitOk = 0;
    public const int ExitUsage = 1;
    public const int ExitNoCable = 2;
    public const int ExitIo = 3;
    public const int ExitPartial = 4;

    public static bool MentionsHelperVerb(IReadOnlyList<string> args)
    {
        if (args is null) return false;
        foreach (var a in args)
            if (IsVerb(a)) return true;
        return false;
    }

    public static bool IsVerb(string? a) =>
        a == Configure || a == Restore;

    /// <summary>
    /// Returns true when <paramref name="args"/> is exactly one allowed verb.
    /// A mention of a helper verb with extra tokens is a usage error (<paramref name="error"/> set, verb None).
    /// Ordinary GUI flags such as --tray return false with no error.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string>? args, out Verb verb, out string? error)
    {
        verb = Verb.None;
        error = null;
        if (args is null || args.Count == 0) return false;
        bool mentions = MentionsHelperVerb(args);
        if (!mentions) return false;
        if (args.Count != 1)
        {
            error = "helper accepts exactly one argument (--configure-cable or --restore-cable)";
            return false;
        }
        if (args[0] == Configure) { verb = Verb.Configure; return true; }
        if (args[0] == Restore) { verb = Verb.Restore; return true; }
        error = "unknown helper verb";
        return false;
    }
}
