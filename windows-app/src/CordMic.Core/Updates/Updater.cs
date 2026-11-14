using System.Net.Http.Headers;
using System.Text.Json;

namespace CordMic.Core.Updates;

/// <summary>How this copy of CordMic was installed, which decides the update asset and how it is applied.</summary>
public enum InstallKind { Installed, Portable }

/// <summary>Bytes received so far and the expected total (null when unknown).</summary>
public readonly record struct DownloadProgress(long Received, long? Total)
{
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Received / Total.Value, 0, 1) : null;
}

/// <summary>Why an update did not finish. The UI maps each value to a plain-language sentence and a GitHub link.</summary>
public enum UpdateError { None, Network, NoAsset, HashMismatch, TooLarge, NotWritable, Declined, InstallFailed, Downgrade, Unknown }

/// <summary>An update step failed for a reason the user can act on.</summary>
public sealed class UpdateException : Exception
{
    public UpdateError Kind { get; }
    public UpdateException(UpdateError kind, string message, Exception? inner = null) : base(message, inner) { Kind = kind; }
}

/// <summary>
/// A downloaded, hash-verified update (installer or portable zip). <see cref="Handle"/> is a read-only handle that
/// denies writers and deleters, opened before the hash was computed, so the bytes that were checked are the bytes that
/// are run or unpacked. Dispose releases it and removes the private staging directory.
/// </summary>
public sealed class StagedUpdate : IDisposable
{
    public StagedUpdate(string tag, InstallKind kind, string path, string dir, FileStream handle, string? sha256 = null)
    { Tag = tag; Kind = kind; Path = path; Dir = dir; Handle = handle; Sha256 = sha256; }
    /// <summary>Verified SHA-256 (lower-case hex) of <see cref="Path"/>, or null.</summary>
    public string? Sha256 { get; }
    public string Tag { get; }
    public InstallKind Kind { get; }
    public string Path { get; }
    public string Dir { get; }
    public FileStream Handle { get; }
    private bool _keepFiles, _disposed;
    public bool IsDisposed => _disposed;

    /// <summary>Releases the handle but leaves the files for a child process that is now using them.</summary>
    public void ReleaseForChild() { _keepFiles = true; Dispose(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Handle.Dispose(); } catch (Exception) { }
        if (!_keepFiles) SecureFiles.TryDelete(Dir);
    }
}

/// <summary>
/// 0.7.1 (audit M8): <c>ready.json</c> written next to a verified download, so the update can be offered again after a
/// restart. It is only a pointer: at startup the file is re-opened locked and re-hashed, inside a folder that must
/// still be private (<see cref="SecureFiles.IsPrivateDirectory"/>).
/// </summary>
public sealed record ReadyManifest(string Tag, InstallKind Kind, string File, string Sha256)
{
    public const string FileName = "ready.json";

    public static ReadyManifest? TryRead(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > 4096 || fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            var m = JsonSerializer.Deserialize<ReadyManifest>(System.IO.File.ReadAllText(path));
            if (m is null || !AppUpdate.SemVer.TryParse(m.Tag, out _) || !Enum.IsDefined(m.Kind)) return null;
            if (string.IsNullOrEmpty(m.File) || System.IO.Path.GetFileName(m.File) != m.File || m.File == FileName) return null;
            if (m.Sha256 is not { Length: 64 } || !m.Sha256.All(Uri.IsHexDigit)) return null;
            return m;
        }
        catch (Exception) { return null; }
    }

    public void Write(string dir) =>
        SecureFiles.WriteNew(System.IO.Path.Combine(dir, FileName), JsonSerializer.SerializeToUtf8Bytes(this));
}

/// <summary>Outcome of <see cref="UpdateEngine.Restore"/>: the restored update, and whether a ready one was unusable.</summary>
public sealed record RestoreResult(StagedUpdate? Staged, bool Unusable);

/// <summary>
/// Network side of the updater: reads the latest-release metadata, downloads the asset for this
/// <see cref="InstallKind"/> into a new private directory with progress and cancellation, and verifies it against
/// the release's SHA256SUMS on a locked handle (0.6.1 audit M1, kept in 0.7.0).
/// </summary>
public sealed class UpdateEngine
{
    /// <summary>Refuse assets larger than this (a corrupt or hostile size must not fill the disk).</summary>
    public const long MaxAssetBytes = 400L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly string _latestUrl;
    public string CurrentVersion { get; }
    public string StagingRoot { get; }
    /// <summary>A download that receives nothing for this long fails (the HttpClient itself has no overall timeout).</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public UpdateEngine(HttpClient http, string currentVersion, string stagingRoot, string latestUrl = AppUpdate.LatestApiUrl)
    {
        _http = http; CurrentVersion = currentVersion; StagingRoot = stagingRoot; _latestUrl = latestUrl;
    }

    /// <summary>An HttpClient suited to the engine: no overall timeout (the engine applies its own), GitHub headers.</summary>
    public static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)
    {
        var h = handler is null ? new HttpClient() : new HttpClient(handler);
        h.Timeout = Timeout.InfiniteTimeSpan;
        h.DefaultRequestHeaders.UserAgent.ParseAdd(AppUpdate.UserAgent);
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    public async Task<AppUpdate.ReleaseInfo> FetchLatestAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(MetadataTimeout);
        try
        {
            var json = await _http.GetStringAsync(_latestUrl, cts.Token).ConfigureAwait(false);
            return AppUpdate.ParseLatestJson(json);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new UpdateException(UpdateError.Network, "GitHub did not answer in time"); }
        catch (HttpRequestException e) { throw new UpdateException(UpdateError.Network, e.Message, e); }
        catch (System.Text.Json.JsonException e) { throw new UpdateException(UpdateError.Network, "unreadable release information", e); }
    }

    /// <summary>Asset name and URL this install needs from <paramref name="info"/>, or an <see cref="UpdateException"/>.</summary>
    public static (string Name, string Url) AssetFor(AppUpdate.ReleaseInfo info, InstallKind kind)
    {
        var (name, url) = kind == InstallKind.Portable ? (info.PortableName, info.PortableUrl) : (info.SetupName, info.SetupUrl);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
            throw new UpdateException(UpdateError.NoAsset, kind == InstallKind.Portable ? "release has no portable zip" : "release has no Windows installer");
        return (name, url);
    }

    public async Task<StagedUpdate> DownloadAsync(AppUpdate.ReleaseInfo info, InstallKind kind, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (AppUpdate.IsDowngrade(info.Tag, CurrentVersion)) throw new UpdateException(UpdateError.Downgrade, "refusing to downgrade to " + info.Tag);
        if (!AppUpdate.IsNewer(info.Tag, CurrentVersion)) throw new UpdateException(UpdateError.Downgrade, "already up to date (" + CurrentVersion + ")");
        var (name, url) = AssetFor(info, kind);
        if (string.IsNullOrEmpty(info.SumsUrl)) throw new UpdateException(UpdateError.NoAsset, "release has no SHA256SUMS");
        long? listed = info.AssetSizes is { } sz && sz.TryGetValue(name, out var l) ? l : null;
        if (listed > MaxAssetBytes) throw new UpdateException(UpdateError.TooLarge, $"{name} is too large");

        string expected;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(MetadataTimeout);
            string sumsText;
            try { sumsText = await _http.GetStringAsync(info.SumsUrl, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new UpdateException(UpdateError.Network, "SHA256SUMS download timed out"); }
            catch (HttpRequestException e) { throw new UpdateException(UpdateError.Network, e.Message, e); }
            expected = AppUpdate.HashForAsset(AppUpdate.ParseSha256Sums(sumsText), name)
                ?? throw new UpdateException(UpdateError.NoAsset, "SHA256SUMS has no entry for " + name);
        }

        CleanOldStaging(StagingRoot);
        var dir = SecureFiles.CreatePrivateDirectory(StagingRoot, "update-");
        try
        {
            var dest = System.IO.Path.Combine(dir, AppUpdate.SafeAssetFileName(name, kind == InstallKind.Portable ? "CordMic-Portable.zip" : "CordMic-Setup.exe"));
            await DownloadToAsync(url, dest, listed, progress, ct).ConfigureAwait(false);
            FileStream handle;
            try { handle = SecureFiles.OpenLockedVerified(dest, expected); }
            catch (InvalidDataException) { throw new UpdateException(UpdateError.HashMismatch, "the downloaded file does not match SHA256SUMS"); }
            var sha = expected.Trim().ToLowerInvariant();
            try { new ReadyManifest(info.Tag, kind, System.IO.Path.GetFileName(dest), sha).Write(dir); }
            catch (Exception) { /* the update still works this session; it just is not remembered */ }
            return new StagedUpdate(info.Tag, kind, dest, dir, handle, sha);
        }
        catch
        {
            SecureFiles.TryDelete(dir);
            throw;
        }
    }

    private async Task DownloadToAsync(string url, string dest, long? listed, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength ?? listed;
            if (total > MaxAssetBytes) throw new UpdateException(UpdateError.TooLarge, "download is too large");
            await using var input = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var fs = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buf = new byte[81920];
            long got = 0, lastReport = -1;
            progress?.Report(new DownloadProgress(0, total));
            while (true)
            {
                int n = await input.ReadAsync(buf, stall.Token).ConfigureAwait(false);
                if (n == 0) break;
                stall.CancelAfter(StallTimeout);
                got += n;
                if (got > MaxAssetBytes) throw new UpdateException(UpdateError.TooLarge, "download is too large");
                await fs.WriteAsync(buf.AsMemory(0, n), stall.Token).ConfigureAwait(false);
                if (got - lastReport >= 256 * 1024) { lastReport = got; progress?.Report(new DownloadProgress(got, total)); }
            }
            await fs.FlushAsync(stall.Token).ConfigureAwait(false);
            progress?.Report(new DownloadProgress(got, total ?? got));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new UpdateException(UpdateError.Network, "the download stalled"); }
        catch (HttpRequestException e) { throw new UpdateException(UpdateError.Network, e.Message, e); }
        catch (IOException e) { throw new UpdateException(UpdateError.Network, e.Message, e); }
    }

    /// <summary>
    /// Removes staging directories left by earlier runs: older than one hour, or dated more than a day in the future
    /// (clock moved back, audit M5). <paramref name="keep"/> (a restored ready update) is never removed; folders in use
    /// by a running child fail to delete and stay.
    /// </summary>
    public static int CleanOldStaging(string root, string? keep = null, DateTime? nowUtc = null)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(root)) return 0;
            var now = nowUtc ?? DateTime.UtcNow;
            foreach (var d in Directory.GetDirectories(root))
            {
                try
                {
                    if (keep is not null && InstallMode.SamePath(d, keep)) continue;
                    if (IsStale(Directory.GetCreationTimeUtc(d), now)) { SecureFiles.TryDelete(d); if (!Directory.Exists(d)) removed++; }
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return removed;
    }

    /// <summary>Older than one hour, or more than a day in the future.</summary>
    public static bool IsStale(DateTime createdUtc, DateTime nowUtc) =>
        nowUtc - createdUtc > TimeSpan.FromHours(1) || createdUtc - nowUtc > TimeSpan.FromDays(1);

    /// <summary>
    /// 0.7.1 (audit M8): finds the newest ready update of this <paramref name="kind"/> left by an earlier run, checks
    /// that its folder is still private and not a reparse point, re-opens the file locked and re-hashes it on that
    /// handle. Ready folders that are older than the running version, of the other kind, or fail any check are
    /// deleted. <see cref="RestoreResult.Unusable"/> is true when a newer ready update existed but could not be used
    /// (the caller then downloads again at once). Other stale folders are cleaned afterwards.
    /// </summary>
    public RestoreResult Restore(InstallKind kind, DateTime? nowUtc = null)
    {
        StagedUpdate? restored = null;
        bool unusable = false;
        try
        {
            if (Directory.Exists(StagingRoot))
            {
                var cands = new List<(AppUpdate.SemVer V, string Dir, ReadyManifest M)>();
                foreach (var d in Directory.GetDirectories(StagingRoot, "update-*"))
                {
                    var mf = System.IO.Path.Combine(d, ReadyManifest.FileName);
                    if (!System.IO.File.Exists(mf)) continue;
                    var m = ReadyManifest.TryRead(mf);
                    if (m is null) { unusable = true; SecureFiles.TryDelete(d); continue; }
                    if (m.Kind != kind || !AppUpdate.IsNewer(m.Tag, CurrentVersion) || !AppUpdate.SemVer.TryParse(m.Tag, out var v))
                    { SecureFiles.TryDelete(d); continue; }
                    cands.Add((v, d, m));
                }
                foreach (var (_, d, m) in cands.OrderByDescending(c => c.V))
                {
                    if (restored is not null) { SecureFiles.TryDelete(d); continue; }
                    try
                    {
                        if (!SecureFiles.IsPrivateDirectory(d)) throw new IOException("staging folder is no longer private");
                        var path = System.IO.Path.Combine(d, m.File);
                        var handle = SecureFiles.OpenLockedVerified(path, m.Sha256);
                        restored = new StagedUpdate(m.Tag, kind, path, d, handle, m.Sha256);
                    }
                    catch (Exception) { unusable = true; SecureFiles.TryDelete(d); }
                }
            }
        }
        catch (Exception) { }
        CleanOldStaging(StagingRoot, restored?.Dir, nowUtc);
        return new RestoreResult(restored, unusable && restored is null);
    }
}

/// <summary>Starts a verified update. Implementations throw <see cref="UpdateException"/> (Declined when the user said no at UAC).</summary>
public interface IUpdateInstaller
{
    Task InstallAsync(StagedUpdate staged, CancellationToken ct);
}

public enum UpdatePhase { Idle, Checking, UpToDate, Available, Downloading, Ready, Installing, Failed }

/// <summary>Snapshot of the updater for the UI.</summary>
public sealed record UpdateState(
    UpdatePhase Phase,
    string? Tag = null,
    DownloadProgress Progress = default,
    UpdateError Error = UpdateError.None,
    string? Detail = null,
    string? ReleasePage = null,
    bool Cancelled = false)
{
    /// <summary>A newer version is downloaded, verified and waiting for the user (tray dot, Updates badge).</summary>
    public bool ReadyToInstall => Phase == UpdatePhase.Ready;
    public bool Busy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;
}

/// <summary>
/// 0.7.0 updater state machine. Only one check/download runs at a time: callers that arrive while one is running get
/// the same task. <see cref="UpdateNowAsync"/> is the one-click path (check, download, verify, install); background
/// checks stop at <see cref="UpdatePhase.Ready"/> and never install on their own.
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly UpdateEngine _engine;
    private readonly IUpdateInstaller _installer;
    private readonly Func<bool> _canApply;
    private Task? _op;
    private CancellationTokenSource? _cts;
    private StagedUpdate? _staged;
    private UpdateState _state = new(UpdatePhase.Idle);
    private bool _installing, _disposed;
    private DateTime? _lastCheckUtc;
    private string? _lastPage;
    private readonly CancellationTokenSource _life = new();

    public InstallKind Kind { get; }
    public UpdateState State { get { lock (_gate) return _state; } }
    public StagedUpdate? Staged { get { lock (_gate) return _staged; } }
    public DateTime? LastCheckUtc { get { lock (_gate) return _lastCheckUtc; } }
    /// <summary>True from the moment an install is claimed until the installer has started (or failed).</summary>
    public bool Installing { get { lock (_gate) return _installing; } }
    /// <summary>
    /// 0.7.1 (audit M9): if CordMic is still running this long after the installer started, the install is reported as
    /// failed so the user can retry or use the download page.
    /// </summary>
    public TimeSpan InstallWatchdog { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>Raised on the thread that changed the state (the app marshals to the UI thread).</summary>
    public event Action<UpdateState>? Changed;
    /// <summary>Raised once when a background check has a verified update ready.</summary>
    public event Action<string>? ReadyInBackground;
    public Action<string>? Log { get; set; }

    /// <param name="canApply">False when this copy cannot replace itself (portable copy in a read-only folder).</param>
    public UpdateCoordinator(UpdateEngine engine, InstallKind kind, IUpdateInstaller installer, Func<bool>? canApply = null)
    {
        _engine = engine; Kind = kind; _installer = installer; _canApply = canApply ?? (() => true);
    }

    private void Set(UpdateState s)
    {
        lock (_gate) { if (_disposed) return; _state = s; }
        try { Changed?.Invoke(s); } catch (Exception) { }
    }

    private void Say(string msg)
    {
        lock (_gate) if (_disposed) return;
        try { Log?.Invoke(msg); } catch (Exception) { }
    }

    private string? Page { get { lock (_gate) return _lastPage; } }

    /// <summary>Checks and (when <paramref name="download"/>) downloads and verifies in the background. Never installs.</summary>
    public Task CheckAsync(bool download)
    {
        lock (_gate)
        {
            if (_disposed || _installing) return Task.CompletedTask; // audit H1: never check while an install runs
            if (_op is { IsCompleted: false } running) return running;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _op = Task.Run(() => RunCheckAsync(download, background: true, ct));
            return _op;
        }
    }

    /// <summary>
    /// 0.7.1 (audit M8): at startup, restores a verified update left by an earlier run (Install is available at once).
    /// If a ready update existed but is unusable and <paramref name="autoDownload"/> is on, or a check is due, a check
    /// (and download) runs right away in the same single-flight operation.
    /// </summary>
    public Task StartupAsync(bool autoDownload, bool checkDue)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_op is { IsCompleted: false } running) return running;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _op = Task.Run(() => RunStartupAsync(autoDownload, checkDue, ct));
            return _op;
        }
    }

    private async Task RunStartupAsync(bool autoDownload, bool checkDue, CancellationToken ct)
    {
        RestoreResult r;
        try { r = _engine.Restore(Kind); }
        catch (Exception e) { Say("update: restore failed: " + e.Message); r = new RestoreResult(null, false); }
        if (r.Staged is { } s)
        {
            bool keep;
            lock (_gate) { keep = !_disposed && _staged is null; if (keep) _staged = s; }
            if (!keep) { s.Dispose(); return; }
            Say($"update {s.Tag}: verified download from an earlier run is ready");
            Set(new UpdateState(UpdatePhase.Ready, s.Tag, ReleasePage: AppUpdate.ReleasesPageUrl));
        }
        else if (r.Unusable) Say("update: the update downloaded earlier could not be used" + (autoDownload ? "; downloading it again" : ""));
        if (ct.IsCancellationRequested) return;
        if (checkDue || (r.Unusable && autoDownload)) await RunCheckAsync(autoDownload, background: true, ct).ConfigureAwait(false);
    }

    /// <summary>One click: waits for a running check, then downloads if needed, verifies and starts the install.</summary>
    public async Task UpdateNowAsync()
    {
        Task? running;
        lock (_gate)
        {
            if (_disposed || _installing) return;
            running = _op is { IsCompleted: false } r ? r : null;
        }
        if (running != null) { try { await running.ConfigureAwait(false); } catch (Exception) { } }
        if (State.Cancelled && running != null) return; // the user cancelled the run we joined
        if (Staged is null)
        {
            Task t;
            lock (_gate)
            {
                if (_disposed || _installing) return;
                if (_op is { IsCompleted: false } again) t = again;
                else
                {
                    _cts?.Dispose();
                    _cts = new CancellationTokenSource();
                    var ct = _cts.Token;
                    t = _op = Task.Run(() => RunCheckAsync(download: true, background: false, ct));
                }
            }
            try { await t.ConfigureAwait(false); } catch (Exception) { }
        }
        if (Staged is null) return;
        await InstallStagedAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the staged update (UAC for the installer). A declined prompt keeps it ready. 0.7.1 (audit H1): waits for
    /// a running check first, then claims the install under the lock, so only one install can run and no check can
    /// drop or re-flag the update meanwhile.
    /// </summary>
    public async Task InstallStagedAsync()
    {
        Task? running;
        lock (_gate) running = _op is { IsCompleted: false } r ? r : null;
        if (running != null) { try { await running.ConfigureAwait(false); } catch (Exception) { } }
        StagedUpdate? s;
        lock (_gate)
        {
            s = _staged;
            if (_disposed || _installing || s is null || s.IsDisposed) return;
            if (_op is { IsCompleted: false }) return; // a new check started in between: the user can click again
            _installing = true;
        }
        Set(new UpdateState(UpdatePhase.Installing, s.Tag, ReleasePage: Page));
        bool started = false;
        try
        {
            await _installer.InstallAsync(s, CancellationToken.None).ConfigureAwait(false);
            started = true;
            lock (_gate) if (ReferenceEquals(_staged, s)) _staged = null;
            Say($"update {s.Tag}: installer started");
        }
        catch (UpdateException e) when (e.Kind == UpdateError.Declined)
        {
            Say("update: Windows permission declined; the update stays ready");
            Set(new UpdateState(UpdatePhase.Ready, s.Tag, Error: UpdateError.Declined, ReleasePage: Page));
        }
        catch (Exception e)
        {
            var kind = e is UpdateException ue ? ue.Kind : UpdateError.InstallFailed;
            Say("update: install failed: " + e.Message);
            Set(new UpdateState(UpdatePhase.Ready, s.Tag, Error: kind, Detail: e.Message, ReleasePage: Page));
        }
        finally
        {
            lock (_gate) _installing = false;
        }
        if (started) _ = WatchInstallAsync(s.Tag);
    }

    /// <summary>Audit M9: CordMic should have been closed by now; if not, report it so the user can act.</summary>
    private async Task WatchInstallAsync(string tag)
    {
        try { await Task.Delay(InstallWatchdog, _life.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }
        bool stuck;
        lock (_gate) stuck = !_disposed && _state.Phase == UpdatePhase.Installing && _state.Tag == tag;
        if (!stuck) return;
        Say($"update {tag}: CordMic was not closed by the update; reporting it as failed");
        Set(new UpdateState(UpdatePhase.Failed, tag, Error: UpdateError.InstallFailed, Detail: "CordMic was not restarted", ReleasePage: Page ?? AppUpdate.ReleasesPageUrl));
    }

    /// <summary>Cancels a running check or download. A staged update is kept.</summary>
    public void Cancel()
    {
        lock (_gate) { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }
    }

    private async Task RunCheckAsync(bool download, bool background, CancellationToken ct)
    {
        string? tag = null;
        try
        {
            var keepTag = Staged is { IsDisposed: false } st0 ? st0.Tag : null;
            Set(keepTag is null ? new UpdateState(UpdatePhase.Checking, ReleasePage: Page) : new UpdateState(UpdatePhase.Checking, keepTag, ReleasePage: Page));
            var info = await _engine.FetchLatestAsync(ct).ConfigureAwait(false);
            lock (_gate) { _lastCheckUtc = DateTime.UtcNow; _lastPage = info.HtmlUrl ?? AppUpdate.ReleasesPageUrl; }
            tag = info.Tag;
            if (!AppUpdate.IsNewer(info.Tag, _engine.CurrentVersion))
            {
                DropStaged();
                Set(new UpdateState(UpdatePhase.UpToDate, info.Tag, ReleasePage: Page));
                return;
            }
            if (Staged is { IsDisposed: false } same && same.Tag == info.Tag)
            {
                Set(new UpdateState(UpdatePhase.Ready, info.Tag, ReleasePage: Page));
                return;
            }
            DropStaged();
            if (!download) { Set(new UpdateState(UpdatePhase.Available, info.Tag, ReleasePage: Page)); return; }
            if (!_canApply())
                throw new UpdateException(UpdateError.NotWritable, "this copy of CordMic is in a folder it cannot write to");
            UpdateEngine.AssetFor(info, Kind); // fail early with NoAsset
            Set(new UpdateState(UpdatePhase.Downloading, info.Tag, ReleasePage: Page));
            var progress = new SyncProgress(p => Set(new UpdateState(UpdatePhase.Downloading, info.Tag, p, ReleasePage: Page)));
            var staged = await _engine.DownloadAsync(info, Kind, progress, ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed) { staged.Dispose(); return; }
                _staged = staged;
            }
            Say($"update {info.Tag}: downloaded and verified ({Kind})");
            Set(new UpdateState(UpdatePhase.Ready, info.Tag, ReleasePage: Page));
            if (background) { bool live; lock (_gate) live = !_disposed; if (live) try { ReadyInBackground?.Invoke(info.Tag); } catch (Exception) { } }
        }
        catch (Exception) when (KeepReady())
        {
            // A ready update stays ready when a later check fails or is cancelled.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Say("update: cancelled");
            Set(new UpdateState(tag is null ? UpdatePhase.Idle : UpdatePhase.Available, tag, ReleasePage: Page, Cancelled: true));
        }
        catch (UpdateException e)
        {
            Say($"update: {e.Kind}: {e.Message}");
            Set(new UpdateState(UpdatePhase.Failed, tag, Error: e.Kind, Detail: e.Message, ReleasePage: Page ?? AppUpdate.ReleasesPageUrl));
        }
        catch (Exception e)
        {
            Say("update: " + e.Message);
            Set(new UpdateState(UpdatePhase.Failed, tag, Error: UpdateError.Unknown, Detail: e.Message, ReleasePage: Page ?? AppUpdate.ReleasesPageUrl));
        }
    }

    /// <summary>Exception filter: when a verified update is staged, a failed or cancelled check returns to Ready.</summary>
    private bool KeepReady()
    {
        var s = Staged;
        if (s is null || s.IsDisposed) return false;
        Say("update: check did not finish; the downloaded update stays ready");
        Set(new UpdateState(UpdatePhase.Ready, s.Tag, ReleasePage: Page));
        return true;
    }

    private void DropStaged()
    {
        StagedUpdate? old;
        lock (_gate) { old = _staged; _staged = null; }
        old?.Dispose();
    }

    /// <summary>
    /// Stops everything: cancels a running check or download, drops the in-memory staged update (its folder and
    /// <c>ready.json</c> stay only when the update was handed to a child; otherwise the folder is removed, and a
    /// download that finishes later is discarded) and raises no more events (audit M2).
    /// </summary>
    public void Dispose()
    {
        StagedUpdate? old;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            old = _staged;
            _staged = null;
        }
        try { _life.Cancel(); } catch (ObjectDisposedException) { }
        // Keep a verified ready update on disk so the next start can offer it again (audit M8): release the handle only.
        old?.ReleaseForChild();
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; would post to a captured context).</summary>
    private sealed class SyncProgress : IProgress<DownloadProgress>
    {
        private readonly Action<DownloadProgress> _a;
        public SyncProgress(Action<DownloadProgress> a) => _a = a;
        public void Report(DownloadProgress value) => _a(value);
    }
}
