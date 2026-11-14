using System.Net.Http.Headers;

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
    public StagedUpdate(string tag, InstallKind kind, string path, string dir, FileStream handle)
    { Tag = tag; Kind = kind; Path = path; Dir = dir; Handle = handle; }
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
            return new StagedUpdate(info.Tag, kind, dest, dir, handle);
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

    /// <summary>Removes staging directories left by earlier runs (older than one hour; ones still in use stay).</summary>
    public static void CleanOldStaging(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root))
            {
                try { if (DateTime.UtcNow - Directory.GetCreationTimeUtc(d) > TimeSpan.FromHours(1)) SecureFiles.TryDelete(d); }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
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

    public InstallKind Kind { get; }
    public UpdateState State { get { lock (_gate) return _state; } }
    public StagedUpdate? Staged { get { lock (_gate) return _staged; } }
    public DateTime? LastCheckUtc { get; private set; }
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
        lock (_gate) _state = s;
        try { Changed?.Invoke(s); } catch (Exception) { }
    }

    /// <summary>Checks and (when <paramref name="download"/>) downloads and verifies in the background. Never installs.</summary>
    public Task CheckAsync(bool download)
    {
        lock (_gate)
        {
            if (_op is { IsCompleted: false } running) return running;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _op = Task.Run(() => RunCheckAsync(download, background: true, ct));
            return _op;
        }
    }

    /// <summary>One click: waits for a running check, then downloads if needed, verifies and starts the install.</summary>
    public async Task UpdateNowAsync()
    {
        Task? running;
        lock (_gate) running = _op is { IsCompleted: false } r ? r : null;
        if (running != null) { try { await running.ConfigureAwait(false); } catch (Exception) { } }
        if (State.Cancelled && running != null) return; // the user cancelled the run we joined
        if (Staged is null)
        {
            Task t;
            lock (_gate)
            {
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

    /// <summary>Starts the staged update (UAC for the installer). A declined prompt keeps it ready.</summary>
    public async Task InstallStagedAsync()
    {
        StagedUpdate? s;
        lock (_gate)
        {
            s = _staged;
            if (s is null || s.IsDisposed || _state.Phase == UpdatePhase.Installing) return;
        }
        Set(new UpdateState(UpdatePhase.Installing, s.Tag, ReleasePage: _lastPage));
        try
        {
            await _installer.InstallAsync(s, CancellationToken.None).ConfigureAwait(false);
            lock (_gate) if (ReferenceEquals(_staged, s)) _staged = null;
            Log?.Invoke($"update {s.Tag}: installer started");
        }
        catch (UpdateException e) when (e.Kind == UpdateError.Declined)
        {
            Log?.Invoke("update: Windows permission declined; the update stays ready");
            Set(new UpdateState(UpdatePhase.Ready, s.Tag, Error: UpdateError.Declined, ReleasePage: _lastPage));
        }
        catch (Exception e)
        {
            var kind = e is UpdateException ue ? ue.Kind : UpdateError.InstallFailed;
            Log?.Invoke("update: install failed: " + e.Message);
            Set(new UpdateState(UpdatePhase.Ready, s.Tag, Error: kind, Detail: e.Message, ReleasePage: _lastPage));
        }
    }

    /// <summary>Cancels a running check or download. A staged update is kept.</summary>
    public void Cancel()
    {
        lock (_gate) { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }
    }

    private string? _lastPage;

    private async Task RunCheckAsync(bool download, bool background, CancellationToken ct)
    {
        string? tag = null;
        try
        {
            Set(new UpdateState(UpdatePhase.Checking, ReleasePage: _lastPage));
            var info = await _engine.FetchLatestAsync(ct).ConfigureAwait(false);
            LastCheckUtc = DateTime.UtcNow;
            _lastPage = info.HtmlUrl ?? AppUpdate.ReleasesPageUrl;
            tag = info.Tag;
            if (!AppUpdate.IsNewer(info.Tag, _engine.CurrentVersion))
            {
                DropStaged();
                Set(new UpdateState(UpdatePhase.UpToDate, info.Tag, ReleasePage: _lastPage));
                return;
            }
            if (Staged is { IsDisposed: false } same && same.Tag == info.Tag)
            {
                Set(new UpdateState(UpdatePhase.Ready, info.Tag, ReleasePage: _lastPage));
                return;
            }
            DropStaged();
            if (!download) { Set(new UpdateState(UpdatePhase.Available, info.Tag, ReleasePage: _lastPage)); return; }
            if (!_canApply())
                throw new UpdateException(UpdateError.NotWritable, "this copy of CordMic is in a folder it cannot write to");
            UpdateEngine.AssetFor(info, Kind); // fail early with NoAsset
            Set(new UpdateState(UpdatePhase.Downloading, info.Tag, ReleasePage: _lastPage));
            var progress = new SyncProgress(p => Set(new UpdateState(UpdatePhase.Downloading, info.Tag, p, ReleasePage: _lastPage)));
            var staged = await _engine.DownloadAsync(info, Kind, progress, ct).ConfigureAwait(false);
            lock (_gate) _staged = staged;
            Log?.Invoke($"update {info.Tag}: downloaded and verified ({Kind})");
            Set(new UpdateState(UpdatePhase.Ready, info.Tag, ReleasePage: _lastPage));
            if (background) ReadyInBackground?.Invoke(info.Tag);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log?.Invoke("update: cancelled");
            Set(new UpdateState(tag is null ? UpdatePhase.Idle : UpdatePhase.Available, tag, ReleasePage: _lastPage, Cancelled: true));
        }
        catch (UpdateException e)
        {
            Log?.Invoke($"update: {e.Kind}: {e.Message}");
            Set(new UpdateState(UpdatePhase.Failed, tag, Error: e.Kind, Detail: e.Message, ReleasePage: _lastPage ?? AppUpdate.ReleasesPageUrl));
        }
        catch (Exception e)
        {
            Log?.Invoke("update: " + e.Message);
            Set(new UpdateState(UpdatePhase.Failed, tag, Error: UpdateError.Unknown, Detail: e.Message, ReleasePage: _lastPage ?? AppUpdate.ReleasesPageUrl));
        }
    }

    private void DropStaged()
    {
        StagedUpdate? old;
        lock (_gate) { old = _staged; _staged = null; }
        old?.Dispose();
    }

    public void Dispose()
    {
        Cancel();
        DropStaged();
        lock (_gate) { _cts?.Dispose(); _cts = null; }
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; would post to a captured context).</summary>
    private sealed class SyncProgress : IProgress<DownloadProgress>
    {
        private readonly Action<DownloadProgress> _a;
        public SyncProgress(Action<DownloadProgress> a) => _a = a;
        public void Report(DownloadProgress value) => _a(value);
    }
}
