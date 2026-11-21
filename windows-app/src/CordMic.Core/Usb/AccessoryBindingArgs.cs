namespace CordMic.Core.Usb;

/// <summary>
/// Elevated helper verbs for the optional accessory binding. Exactly one flag, no paths
/// or OEM names from the command line (those come from the well-known package folder
/// and from %ProgramData%\CordMic\driver.json).
/// </summary>
public static class AccessoryBindingArgs
{
    public const string Install = "--install-accessory-binding";
    public const string Remove = "--remove-accessory-binding";
    public const string Status = "--accessory-binding-status";

    public enum Verb { None, Install, Remove, Status }

    public const int ExitOk = 0;
    public const int ExitUsage = 1;
    public const int ExitRefused = 2;
    public const int ExitIo = 3;

    public static bool Mentions(IReadOnlyList<string>? args)
    {
        if (args is null) return false;
        foreach (var a in args)
            if (IsVerb(a)) return true;
        return false;
    }

    public static bool IsVerb(string? a) =>
        a == Install || a == Remove || a == Status;

    public static bool TryParse(IReadOnlyList<string>? args, out Verb verb, out string? error)
    {
        verb = Verb.None;
        error = null;
        if (args is null || args.Count == 0) return false;
        if (!Mentions(args)) return false;
        if (args.Count != 1)
        {
            error = "accessory helper accepts exactly one argument";
            return false;
        }
        if (args[0] == Install) { verb = Verb.Install; return true; }
        if (args[0] == Remove) { verb = Verb.Remove; return true; }
        if (args[0] == Status) { verb = Verb.Status; return true; }
        error = "unknown accessory helper verb";
        return false;
    }
}
