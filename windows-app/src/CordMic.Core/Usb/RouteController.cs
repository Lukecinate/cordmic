namespace CordMic.Core.Usb;

public enum RouteState
{
    Idle = 0,
    Probing = 1,
    Handshaking = 2,
    WaitingAccessory = 3,
    WaitingApproval = 4,
    UsbStreaming = 5,
    UsbSuspect = 6,
    TetherWaiting = 7,
    TetherStreaming = 8,
    Backoff = 9,
}

public enum RouteHint { None, FallbackTether, ReplugThenTether, AllowAccessory, BindingNeeded, ExperimentalOff }

public readonly record struct RouteEvent(string Kind, string? DeviceKey = null, string? Reason = null, bool TetherReady = false, bool AfterStart = false);

public sealed class UsbBlocklist
{
    public const int MaxEntries = 32;
    public const int FailuresToBlock = 3;

    public List<UsbBlockEntry> Entries { get; } = new();

    public bool IsBlocked(string? key) =>
        !string.IsNullOrEmpty(key) && Entries.Any(e => e.Key == key && e.Failures >= FailuresToBlock);

    public void RecordFailure(string key, long nowTicks)
    {
        if (string.IsNullOrEmpty(key)) return;
        var e = Entries.FirstOrDefault(x => x.Key == key);
        if (e is null)
        {
            e = new UsbBlockEntry { Key = key };
            Entries.Add(e);
        }
        e.Failures++;
        e.LastUsedUtcTicks = nowTicks;
        Trim(nowTicks);
    }

    public void Clear(string? key)
    {
        if (string.IsNullOrEmpty(key)) { Entries.Clear(); return; }
        Entries.RemoveAll(e => e.Key == key);
    }

    public void RecordSuccess(string key, long nowTicks)
    {
        var e = Entries.FirstOrDefault(x => x.Key == key);
        if (e is null) return;
        e.Failures = 0;
        e.LastUsedUtcTicks = nowTicks;
    }

    private void Trim(long nowTicks)
    {
        if (Entries.Count <= MaxEntries) return;
        Entries.Sort((a, b) => a.LastUsedUtcTicks.CompareTo(b.LastUsedUtcTicks));
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    }
}

public sealed class UsbBlockEntry
{
    public string Key { get; set; } = "";
    public int Failures { get; set; }
    public long LastUsedUtcTicks { get; set; }
}

/// <summary>Single-threaded route actor. Tests call <see cref="Post"/> in order on one thread.</summary>
public sealed class RouteController
{
    public RouteState State { get; private set; } = RouteState.Idle;
    public RouteHint Hint { get; private set; }
    public string? DeviceKey { get; private set; }
    public int HandshakeAttemptsThisPlugin { get; private set; }
    public bool Armed { get; private set; }
    public bool ExperimentalUsb { get; set; }
    public string Mode { get; set; } = UsbConnectionModeName.Auto;
    public bool KillSwitch { get; set; }
    public UsbBlocklist Blocklist { get; } = new();
    public List<string> Log { get; } = new();
    public int UsbHealthUnhealthyTicks { get; private set; }
    public int LossesInWindow { get; private set; }

    public event Action<RouteState, RouteState, string>? Transitioned;

    public void Post(RouteEvent ev)
    {
        switch (ev.Kind)
        {
            case "Arm":
                Armed = true;
                if (!UsbPermitted) { Go(RouteState.TetherWaiting, "arm-tether"); return; }
                Go(RouteState.Probing, "arm");
                return;
            case "Disarm":
                Armed = false;
                HandshakeAttemptsThisPlugin = 0;
                DeviceKey = null;
                Go(RouteState.Idle, ev.Reason ?? "disarm");
                return;
            case "PowerSuspend":
                Armed = Armed; // remember
                Go(RouteState.Idle, "suspend");
                return;
            case "Resume":
                if (Armed) Post(new RouteEvent("Arm", Reason: "resume"));
                return;
            case "UserPrefChanged":
                Mode = ev.Reason ?? Mode;
                if (KillSwitch || UsbConnectionModeName.IsTetherOnly(Mode) || !UsbPermitted)
                {
                    if (State is RouteState.UsbStreaming or RouteState.UsbSuspect or RouteState.Handshaking
                        or RouteState.WaitingAccessory or RouteState.WaitingApproval or RouteState.Probing or RouteState.Backoff)
                        Go(RouteState.TetherWaiting, "pref-tether");
                }
                return;
            case "TryUsbAgain":
                Blocklist.Clear(ev.DeviceKey);
                if (Armed && UsbPermitted && State is RouteState.TetherWaiting or RouteState.TetherStreaming or RouteState.Backoff)
                {
                    if (State != RouteState.TetherStreaming) Go(RouteState.Probing, "retry");
                }
                return;
            case "TetherReady":
                OnTetherReady();
                return;
            case "TetherLost":
                if (State == RouteState.TetherStreaming)
                    Go(Armed ? (UsbPermitted ? RouteState.Probing : RouteState.TetherWaiting) : RouteState.Idle, "tether-lost");
                return;
            case "DeviceArrived":
                OnArrived(ev);
                return;
            case "DeviceRemoved":
                if (ev.DeviceKey == DeviceKey && State is RouteState.UsbStreaming or RouteState.UsbSuspect or RouteState.Handshaking or RouteState.WaitingAccessory or RouteState.WaitingApproval)
                    Go(RouteState.Probing, "removed");
                return;
            case "HandshakeOk":
                if (State is RouteState.Handshaking or RouteState.WaitingAccessory or RouteState.WaitingApproval)
                    Go(RouteState.WaitingApproval, "handshake-ok");
                return;
            case "AccessoryOpened":
                if (State is RouteState.WaitingApproval or RouteState.WaitingAccessory or RouteState.Handshaking)
                    Go(RouteState.UsbStreaming, "opened");
                return;
            case "HandshakeFail":
                OnHandshakeFail(ev);
                return;
            case "LinkLost":
                OnLinkLost(ev);
                return;
            case "HealthUnhealthy":
                if (State == RouteState.UsbStreaming)
                {
                    UsbHealthUnhealthyTicks++;
                    Go(RouteState.UsbSuspect, ev.Reason ?? "health");
                }
                else if (State == RouteState.UsbSuspect)
                {
                    UsbHealthUnhealthyTicks++;
                    if (UsbHealthUnhealthyTicks >= 3) OnLinkLost(new RouteEvent("LinkLost", DeviceKey, "health"));
                }
                return;
            case "HealthOk":
                if (State == RouteState.UsbSuspect) { UsbHealthUnhealthyTicks = 0; Go(RouteState.UsbStreaming, "health-ok"); }
                return;
            case "TimerFired":
                if (State == RouteState.Backoff) Go(UsbPermitted ? RouteState.Probing : RouteState.TetherWaiting, "backoff-done");
                if (State == RouteState.Probing && ev.Reason == "replug-window")
                    Go(RouteState.TetherWaiting, "no-reappear");
                return;
            case "FrameReceived":
                if (State == RouteState.UsbSuspect) { UsbHealthUnhealthyTicks = 0; Go(RouteState.UsbStreaming, "frames"); }
                return;
        }
    }

    public bool UsbPermitted =>
        !KillSwitch && !UsbConnectionModeName.IsTetherOnly(Mode) && ExperimentalUsb;

    private void OnTetherReady()
    {
        // Rule 6.4-2: a ready tether wins; never interrupt or start USB over it.
        if (State == RouteState.UsbStreaming || State == RouteState.UsbSuspect) return;
        if (State == RouteState.TetherStreaming) return;
        HandshakeAttemptsThisPlugin = 1; // consume the one USB attempt? No: we skip USB entirely for this plugin when tether is already ready.
        Go(RouteState.TetherStreaming, "tether-ready");
    }

    private void OnArrived(RouteEvent ev)
    {
        if (!Armed) return;
        if (!UsbPermitted) { if (State == RouteState.Idle) Go(RouteState.TetherWaiting, "gate"); return; }
        if (State == RouteState.TetherStreaming) { Hint = RouteHint.None; return; } // do not switch mid-stream
        if (Blocklist.IsBlocked(ev.DeviceKey))
        {
            Hint = RouteHint.FallbackTether;
            Go(RouteState.TetherWaiting, "blocklisted");
            LogLine("usb: blocklisted dev=" + ev.DeviceKey + " after 3 failed sessions");
            return;
        }
        if (HandshakeAttemptsThisPlugin >= 1)
        {
            Hint = RouteHint.FallbackTether;
            return;
        }
        DeviceKey = ev.DeviceKey;
        HandshakeAttemptsThisPlugin++;
        Go(RouteState.Handshaking, "arrived");
    }

    private void OnHandshakeFail(RouteEvent ev)
    {
        if (ev.AfterStart)
        {
            Hint = RouteHint.ReplugThenTether;
            if (!string.IsNullOrEmpty(DeviceKey)) Blocklist.RecordFailure(DeviceKey, 0);
            Go(RouteState.TetherWaiting, ev.Reason ?? "fail-after-start");
            return;
        }
        Hint = RouteHint.FallbackTether;
        if (!string.IsNullOrEmpty(DeviceKey)) Blocklist.RecordFailure(DeviceKey, 0);
        Go(RouteState.TetherWaiting, ev.Reason ?? "handshake-fail");
    }

    private void OnLinkLost(RouteEvent ev)
    {
        LossesInWindow++;
        if (LossesInWindow >= 2)
        {
            Hint = RouteHint.FallbackTether;
            Go(RouteState.TetherWaiting, ev.Reason ?? "second-loss");
            return;
        }
        HandshakeAttemptsThisPlugin = 0;
        Go(RouteState.Probing, ev.Reason ?? "link-lost");
    }

    private void Go(RouteState next, string reason)
    {
        var from = State;
        if (from == next && reason != "arm") return;
        State = next;
        if (next == RouteState.UsbStreaming) { Hint = RouteHint.None; UsbHealthUnhealthyTicks = 0; }
        if (next == RouteState.Idle) { HandshakeAttemptsThisPlugin = 0; LossesInWindow = 0; }
        if (next == RouteState.Probing && reason == "arm") { HandshakeAttemptsThisPlugin = 0; LossesInWindow = 0; }
        if (next == RouteState.TetherStreaming) HandshakeAttemptsThisPlugin = Math.Max(HandshakeAttemptsThisPlugin, 1);
        LogLine($"route: {from} -> {next} ({reason})");
        Transitioned?.Invoke(from, next, reason);
    }

    private void LogLine(string s)
    {
        if (Log.Count >= 256) Log.RemoveAt(0);
        Log.Add(s);
    }
}
