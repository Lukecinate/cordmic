using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using CordMic.Core;
using Microsoft.Win32;

namespace CordMic.App;

internal sealed record CableEndpointInfo(
    string DeviceId, string FriendlyName, bool IsRender,
    string FormatText, bool FormatOptimal, bool? SysFxDisabled)
{
    public bool NeedsChange => !FormatOptimal || SysFxDisabled == false;
}

internal sealed record CableInspectResult(IReadOnlyList<CableEndpointInfo> Endpoints)
{
    public bool Found => Endpoints.Count > 0;
    public bool NeedsOptimize => Endpoints.Any(e => e.NeedsChange);
}

/// <summary>
/// Elevated helper that writes the VB-CABLE stereo endpoint default format.
/// Invoked only as `CordMic.exe --configure-cable` or `--restore-cable`.
/// It never takes paths, device ids or commands from its arguments, never
/// launches a shell, and never writes outside the verified endpoint keys.
/// </summary>
internal static class CableHelper
{
    public static string BackupPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CordMic", "cable-format-backup.json");

    const ushort VtUi4 = 19;
    const ushort VtBlob = 65;
    const ushort VtLpstr = 31;

    public static int Run(CableHelperArgs.Verb verb)
    {
        try
        {
            Log($"helper start verb={verb} user={Environment.UserName} elevated={IsElevated()}");
            return verb == CableHelperArgs.Verb.Configure ? Configure() : Restore();
        }
        catch (Exception ex)
        {
            Log("helper fatal: " + ex);
            return CableHelperArgs.ExitIo;
        }
    }

    public static CableInspectResult Inspect()
    {
        var targets = EnumerateTargets();
        try
        {
            var list = new List<CableEndpointInfo>();
            foreach (var live in targets)
            {
                byte[] fmt = ReadBlob(live.Dev, Wasapi.DeviceFormatKey) ?? Array.Empty<byte>();
                bool? sysfx = ReadUInt(live.Dev, Wasapi.DisableSysFxKey) is { } v ? v != 0 : null;
                list.Add(new CableEndpointInfo(
                    live.Id, live.Name, live.IsRender,
                    WaveFormatBlob.Describe(fmt),
                    WaveFormatBlob.IsOptimalStereo48k(fmt),
                    sysfx));
            }
            return new CableInspectResult(list);
        }
        finally { foreach (var t in targets) Release(t.Dev); }
    }

    static int Configure()
    {
        var targets = EnumerateTargets();
        if (targets.Count == 0)
        {
            Log("no stereo VB-Audio Virtual Cable endpoints found");
            return CableHelperArgs.ExitNoCable;
        }

        if (!EnsureBackup(targets))
            return CableHelperArgs.ExitIo;

        var desired = WaveFormatBlob.PcmStereo48k(16);
        int failed = 0, changed = 0, already = 0;
        foreach (var t in targets)
        {
            try
            {
                byte[] current = ReadBlob(t.Dev, Wasapi.DeviceFormatKey) ?? Array.Empty<byte>();
                bool fmtOk = WaveFormatBlob.IsOptimalStereo48k(current);
                uint? sysfx = ReadUInt(t.Dev, Wasapi.DisableSysFxKey);
                bool sysOk = sysfx == 1;
                if (fmtOk && sysOk)
                {
                    already++;
                    Log($"already optimal: {t.Name} ({WaveFormatBlob.Describe(current)}, SysFx disabled)");
                    continue;
                }
                if (!fmtOk)
                {
                    Log($"set DeviceFormat on {t.Name} ({t.Id}): {WaveFormatBlob.Describe(current)} -> {WaveFormatBlob.Describe(desired)}");
                    if (!WriteBlob(t, Wasapi.DeviceFormatKey, CableBackup.DeviceFormatKey, desired))
                    { failed++; continue; }
                    // Mirror into OEMFormat so the Sound CPL keeps the same default.
                    WriteBlob(t, Wasapi.OemFormatKey, CableBackup.OemFormatKey, desired);
                }
                if (!sysOk)
                {
                    Log($"disable enhancements on {t.Name} ({t.Id}): Disable_SysFx {sysfx?.ToString() ?? "unset"} -> 1");
                    if (!WriteUInt(t, Wasapi.DisableSysFxKey, CableBackup.DisableSysFxKey, 1))
                    { failed++; continue; }
                }
                changed++;
            }
            catch (Exception ex)
            {
                Log($"failed on {t.Name}: {ex.Message}");
                failed++;
            }
        }
        foreach (var t in targets) Release(t.Dev);
        Log($"configure done changed={changed} already={already} failed={failed}");
        if (failed > 0 && changed == 0) return CableHelperArgs.ExitIo;
        if (failed > 0) return CableHelperArgs.ExitPartial;
        return CableHelperArgs.ExitOk;
    }

    static int Restore()
    {
        if (!File.Exists(BackupPath))
        {
            Log("no backup at " + BackupPath + "; nothing to restore");
            return CableHelperArgs.ExitOk;
        }
        string json;
        try { json = File.ReadAllText(BackupPath); }
        catch (Exception ex)
        {
            Log("could not read backup: " + ex.Message);
            return CableHelperArgs.ExitIo;
        }
        if (!CableBackup.TryDeserialize(json, out var file, out var err) || file is null)
        {
            Log("backup rejected: " + err);
            return CableHelperArgs.ExitIo;
        }

        var live = EnumerateTargets().ToDictionary(t => t.Id, t => t, StringComparer.OrdinalIgnoreCase);
        int restored = 0, failed = 0, skipped = 0;
        foreach (var ep in file.Endpoints)
        {
            if (!live.TryGetValue(ep.DeviceId, out var t))
            {
                Log($"skip restore of {ep.DeviceId}: endpoint is not a live VB-CABLE stereo target");
                skipped++;
                continue;
            }
            if (!string.Equals(t.Name, ep.FriendlyName, StringComparison.OrdinalIgnoreCase)
                && !CableIdentity.IsHelperTarget(t.Id, t.Name, t.Manufacturer, t.IsRender))
            {
                Log($"skip restore of {ep.DeviceId}: live name '{t.Name}' no longer matches");
                skipped++;
                continue;
            }
            foreach (var prop in ep.Properties)
            {
                if (!CableBackup.IsAllowedKey(prop.Key) || !CableBackup.TryParseKey(prop.Key, out var fmtid, out var pid))
                {
                    Log("skip disallowed key " + prop.Key);
                    continue;
                }
                var key = new PropertyKey(fmtid, pid);
                try
                {
                    if (prop.Type == "blob" && CableBackup.TryParseHex(prop.Hex ?? "", out var blob))
                    {
                        Log($"restore {prop.Key} on {t.Name}: {WaveFormatBlob.Describe(blob)}");
                        if (!WriteBlob(t, key, prop.Key, blob)) failed++;
                        else restored++;
                    }
                    else if (prop.Type == "ui4" && prop.Dword is { } dw)
                    {
                        Log($"restore {prop.Key} on {t.Name}: {dw}");
                        if (!WriteUInt(t, key, prop.Key, dw)) failed++;
                        else restored++;
                    }
                }
                catch (Exception ex)
                {
                    Log($"restore {prop.Key} on {t.Name} failed: {ex.Message}");
                    failed++;
                }
            }
        }
        foreach (var t in live.Values) Release(t.Dev);
        Log($"restore done restored={restored} skipped={skipped} failed={failed}");
        if (failed > 0 && restored == 0) return CableHelperArgs.ExitIo;
        if (failed > 0) return CableHelperArgs.ExitPartial;
        return CableHelperArgs.ExitOk;
    }

    static bool EnsureBackup(List<Live> targets)
    {
        if (File.Exists(BackupPath))
        {
            Log("keeping existing backup " + BackupPath);
            return true;
        }
        var file = new CableBackupFile
        {
            Version = CableBackupFile.CurrentVersion,
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            AppVersion = typeof(CableHelper).Assembly.GetName().Version?.ToString() ?? "",
        };
        foreach (var t in targets)
        {
            var ep = new CableEndpointBackup
            {
                DeviceId = t.Id,
                FriendlyName = t.Name,
                IsRender = t.IsRender,
            };
            AddBlob(ep, t, Wasapi.DeviceFormatKey, CableBackup.DeviceFormatKey);
            AddBlob(ep, t, Wasapi.OemFormatKey, CableBackup.OemFormatKey);
            if (ReadUInt(t.Dev, Wasapi.DisableSysFxKey) is { } dw)
                ep.Properties.Add(new CablePropertyBackup { Key = CableBackup.DisableSysFxKey, Type = "ui4", Dword = dw });
            file.Endpoints.Add(ep);
        }
        try
        {
            string dir = Path.GetDirectoryName(BackupPath)!;
            Directory.CreateDirectory(dir);
            RestrictAcl(dir);
            File.WriteAllText(BackupPath, CableBackup.Serialize(file));
            RestrictFileAcl(BackupPath);
            Log("wrote backup " + BackupPath);
            return true;
        }
        catch (Exception ex)
        {
            Log("backup write failed (no changes made): " + ex.Message);
            return false;
        }
    }

    static void AddBlob(CableEndpointBackup ep, Live t, PropertyKey key, string name)
    {
        var blob = ReadBlob(t.Dev, key);
        if (blob is null) return;
        ep.Properties.Add(new CablePropertyBackup { Key = name, Type = "blob", Hex = CableBackup.ToHex(blob) });
    }

    sealed class Live
    {
        public required IMMDevice Dev;
        public required string Id;
        public required string Name;
        public string? Manufacturer;
        public required bool IsRender;
    }

    static List<Live> EnumerateTargets()
    {
        var list = new List<Live>();
        var com = Wasapi.CreateEnumerator();
        var en = (IMMDeviceEnumerator)com;
        try
        {
            Collect(en, Wasapi.Render, wantRender: true, list);
            Collect(en, Wasapi.Capture, wantRender: false, list);
        }
        finally { Release(en); }
        return list;
    }

    static void Collect(IMMDeviceEnumerator en, int flow, bool wantRender, List<Live> list)
    {
        if (en.EnumAudioEndpoints(flow, Wasapi.DeviceActive, out var col) < 0) return;
        try
        {
            if (col.GetCount(out int n) < 0) return;
            for (int i = 0; i < n; i++)
            {
                if (col.Item(i, out var dev) < 0) continue;
                try
                {
                    string id = WasapiDevices.IdOf(dev);
                    string name = WasapiDevices.NameOf(dev);
                    string? maker = ReadString(dev, Wasapi.ManufacturerKey);
                    if (!CableIdentity.IsHelperTarget(id, name, maker, wantRender))
                        continue;
                    list.Add(new Live { Dev = dev, Id = id, Name = name, Manufacturer = maker, IsRender = wantRender });
                    dev = null!; // ownership transferred
                }
                finally { if (dev is not null) Release(dev); }
            }
        }
        finally { Release(col); }
    }

    static bool WriteBlob(Live t, PropertyKey key, string keyName, byte[] data)
    {
        if (!ValidateWrite(t, keyName)) return false;
        if (TryStoreBlob(t.Dev, key, data)) return true;
        Log("property store blob write failed, trying registry fallback for " + keyName);
        return TryRegistryWrite(t, keyName, data, dword: null);
    }

    static bool WriteUInt(Live t, PropertyKey key, string keyName, uint value)
    {
        if (!ValidateWrite(t, keyName)) return false;
        if (TryStoreUInt(t.Dev, key, value)) return true;
        Log("property store ui4 write failed, trying registry fallback for " + keyName);
        return TryRegistryWrite(t, keyName, null, value);
    }

    static bool ValidateWrite(Live t, string keyName)
    {
        if (!CableBackup.IsAllowedKey(keyName))
        {
            Log("refusing to write disallowed key " + keyName);
            return false;
        }
        if (!CableIdentity.IsHelperTarget(t.Id, t.Name, t.Manufacturer, t.IsRender))
        {
            Log("refusing to write non-target " + t.Id);
            return false;
        }
        return true;
    }

    static bool TryStoreBlob(IMMDevice dev, PropertyKey key, byte[] data)
    {
        if (dev.OpenPropertyStore(Wasapi.StgmReadWrite, out var store) < 0) return false;
        var pv = new PropVariant { vt = VtBlob, blobSize = (uint)data.Length, blobData = Marshal.AllocCoTaskMem(data.Length) };
        try
        {
            Marshal.Copy(data, 0, pv.blobData, data.Length);
            int hr = store.SetValue(ref key, ref pv);
            if (hr < 0) { Log($"IPropertyStore.SetValue blob hr=0x{(uint)hr:X8}"); return false; }
            hr = store.Commit();
            if (hr < 0) { Log($"IPropertyStore.Commit blob hr=0x{(uint)hr:X8}"); return false; }
            return true;
        }
        finally
        {
            Wasapi.PropVariantClear(ref pv);
            Release(store);
        }
    }

    static bool TryStoreUInt(IMMDevice dev, PropertyKey key, uint value)
    {
        if (dev.OpenPropertyStore(Wasapi.StgmReadWrite, out var store) < 0) return false;
        var pv = new PropVariant { vt = VtUi4, ulVal = value };
        try
        {
            int hr = store.SetValue(ref key, ref pv);
            if (hr < 0) { Log($"IPropertyStore.SetValue ui4 hr=0x{(uint)hr:X8}"); return false; }
            hr = store.Commit();
            if (hr < 0) { Log($"IPropertyStore.Commit ui4 hr=0x{(uint)hr:X8}"); return false; }
            return true;
        }
        finally
        {
            Wasapi.PropVariantClear(ref pv);
            Release(store);
        }
    }

    static bool TryRegistryWrite(Live t, string valueName, byte[]? blob, uint? dword)
    {
        if (!CableIdentity.TryParseEndpointId(t.Id, out bool isRender, out string guid)) return false;
        if (isRender != t.IsRender) return false;
        string path = CableIdentity.RegistryPropertiesPath(isRender, guid) + @"\Properties";
        // Fail closed: the path is built only from the enumerated, parsed device id.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
            if (key is null)
            {
                Log("registry key not found: HKLM\\" + path);
                return false;
            }
            if (blob is not null) key.SetValue(valueName, blob, RegistryValueKind.Binary);
            else if (dword is not null) key.SetValue(valueName, unchecked((int)dword.Value), RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex)
        {
            Log("registry write failed: " + ex.Message);
            return false;
        }
    }

    static byte[]? ReadBlob(IMMDevice dev, PropertyKey key)
    {
        if (dev.OpenPropertyStore(Wasapi.StgmRead, out var store) < 0) return null;
        try
        {
            if (store.GetValue(ref key, out var v) < 0) return null;
            try
            {
                if (v.vt != VtBlob || v.blobData == IntPtr.Zero || v.blobSize == 0 || v.blobSize > 4096) return null;
                var buf = new byte[v.blobSize];
                Marshal.Copy(v.blobData, buf, 0, buf.Length);
                return buf;
            }
            finally { Wasapi.PropVariantClear(ref v); }
        }
        finally { Release(store); }
    }

    static uint? ReadUInt(IMMDevice dev, PropertyKey key)
    {
        if (dev.OpenPropertyStore(Wasapi.StgmRead, out var store) < 0) return null;
        try
        {
            if (store.GetValue(ref key, out var v) < 0) return null;
            try { return v.vt == VtUi4 ? v.ulVal : null; }
            finally { Wasapi.PropVariantClear(ref v); }
        }
        finally { Release(store); }
    }

    static string? ReadString(IMMDevice dev, PropertyKey key)
    {
        if (dev.OpenPropertyStore(Wasapi.StgmRead, out var store) < 0) return null;
        try
        {
            if (store.GetValue(ref key, out var v) < 0) return null;
            try
            {
                if (v.vt == VtLpstr && v.ptr != IntPtr.Zero) return Marshal.PtrToStringUni(v.ptr);
                return null;
            }
            finally { Wasapi.PropVariantClear(ref v); }
        }
        finally { Release(store); }
    }

    static void RestrictAcl(string dir)
    {
        var info = new DirectoryInfo(dir);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        void Allow(WellKnownSidType sid, FileSystemRights rights)
        {
            sec.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
        info.SetAccessControl(sec);
    }

    static void RestrictFileAcl(string path)
    {
        var info = new FileInfo(path);
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(true, false);
        void Allow(WellKnownSidType sid, FileSystemRights rights) =>
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), rights, AccessControlType.Allow));
        Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.Read);
        info.SetAccessControl(sec);
    }

    static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    static void Release(object? com)
    {
        if (com is null) return;
        try { Marshal.ReleaseComObject(com); } catch (Exception) { }
    }

    static void Log(string msg)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg;
        try { Console.Error.WriteLine(line); } catch (Exception) { }
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CordMic", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "cable-helper.log"), line + Environment.NewLine);
        }
        catch (Exception) { }
    }
}
