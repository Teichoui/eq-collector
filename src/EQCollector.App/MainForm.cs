using System.Diagnostics;
using System.Security.Principal;
using EQCollector.Core;

namespace EQCollector.App;

public sealed class MainForm : Form
{
    private const int MaxLogLines = 1500;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly object _engineLock = new();

    private readonly RadioButton _liveMode = new() { Text = "Live capture (while you play)", AutoSize = true, Checked = true };
    private readonly RadioButton _fileMode = new() { Text = "Capture file", AutoSize = true };
    private readonly ComboBox _adapter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly TextBox _file = new() { Width = 420 };
    private readonly Button _browseFile = new() { Text = "Browse...", AutoSize = true };
    private readonly TextBox _out = new() { Width = 420 };
    private readonly Button _browseOut = new() { Text = "Browse...", AutoSize = true };
    private readonly TextBox _eq = new() { Width = 420 };
    private readonly Button _browseEq = new() { Text = "Browse...", AutoSize = true };
    private readonly Button _start = new() { Text = "Start", Width = 110, Height = 32 };
    private readonly Button _stop = new() { Text = "Stop", Width = 110, Height = 32, Enabled = false };
    private readonly Button _saveAa = new() { Text = "Save AAs now", AutoSize = true };
    private readonly Button _openOut = new() { Text = "Open output folder", AutoSize = true };
    private readonly CheckBox _upload = new() { Text = "Upload new items and AA ranks to the website while capturing", AutoSize = true };
    private readonly TextBox _server = new() { Width = 250 };
    private readonly TextBox _token = new() { Width = 250, UseSystemPasswordChar = true };
    private readonly Button _testUpload = new() { Text = "Test", AutoSize = true };
    private readonly Button _uploadSaved = new() { Text = "Upload saved files now", AutoSize = true };
    private readonly CheckBox _saveRaw = new() { Text = "Also save the raw capture (.pcap) while capturing, so problems can be analysed", AutoSize = true, Checked = true };
    private readonly Label _state = new() { AutoSize = true, Text = "Ready." };
    private readonly Label _stats = new() { AutoSize = false, Height = 40, Dock = DockStyle.Fill, Font = new Font("Consolas", 10f), Text = "" };   // wraps instead of clipping in a narrow window
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    private List<AdapterInfo> _adapters = new();
    private CollectorEngine? _engine;
    private LiveCapture? _capture;
    private CancellationTokenSource? _cancel;
    private long _fileProgress;                 // written by the file worker, read by the UI timer (Interlocked / Volatile)
    private volatile bool _fileRunning;
    private PcapWriter? _raw;                   // the raw traffic of the live session (when the box is ticked)
    private readonly object _rawLock = new();
    private string _rawFolder = "";                 // read on the UI thread when capture starts; the capture thread never touches a control
    private volatile bool _rawFailed;               // a write failed: stop trying (one log line, not one per packet)
    private DateTime _liveStarted;
    private bool _sessionHintShown;
    private bool _engineClosed;                 // the engine has been flushed and closed: Stop must not do it again
    private Task? _fileTask;
    private AutoUploader? _autoUploader;           // the live capture's background upload
    private CollectorUploader? _liveUploader;
    private Task? _uploadStopTask;                 // the final upload after Stop
    private volatile bool _uploadBusy;             // "Test" / "Upload saved files now" is running

    public MainForm()
    {
        Text = "EQ Collector " + typeof(MainForm).Assembly.GetName().Version?.ToString(3);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); }      // the exe's own icon (eqcollector.ico) for the title bar and taskbar
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { /* the default icon is fine */ }
        ClientSize = new Size(760, 760);
        MinimumSize = new Size(700, 680);
        StartPosition = FormStartPosition.CenterScreen;

        _out.Text = _settings.OutputFolder;
        _eq.Text = _settings.EqFolder;
        _file.Text = _settings.CaptureFile;
        _saveRaw.Checked = _settings.SaveRawCapture;
        _upload.Checked = _settings.UploadEnabled;
        _server.Text = _settings.UploadServer;
        _token.Text = _settings.UploadToken;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(Section("Source",
            Row(_liveMode, _adapter, _refresh),
            Row(_fileMode, _file, _browseFile)), 0, 0);

        layout.Controls.Add(Section("Folders",
            Row(new Label { Text = "Save results to", Width = 110, TextAlign = ContentAlignment.MiddleLeft }, _out, _browseOut),
            Row(new Label { Text = "EverQuest folder", Width = 110, TextAlign = ContentAlignment.MiddleLeft }, _eq, _browseEq),
            new Label { Text = "The EverQuest folder (dbstr_us.txt, spells_us.txt) gives AA titles and descriptions; without it AAs are saved with ids only.", AutoSize = true, ForeColor = SystemColors.GrayText },
            _saveRaw), 0, 1);

        layout.Controls.Add(Section("Website upload (optional)",
            _upload,
            Row(new Label { Text = "Website", Width = 60, TextAlign = ContentAlignment.MiddleLeft }, _server, new Label { Text = "Token", Width = 45, TextAlign = ContentAlignment.MiddleLeft }, _token, _testUpload),
            new Label { Text = "Get your token on the website's capture-token page (account menu). Only items the website lacks and AA ranks are sent -- no character names, no chat, nothing else.", AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = SystemColors.GrayText },
            _uploadSaved), 0, 2);

        layout.Controls.Add(Row(_start, _stop, _saveAa, _openOut, _state), 0, 3);
        layout.Controls.Add(_stats, 0, 4);
        layout.Controls.Add(new Label { Text = "Items are saved as they are found and AAs every few seconds while they arrive. Nothing leaves this computer unless you turn on the website upload above.", AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 6) }, 0, 5);
        layout.Controls.Add(_log, 0, 6);
        Controls.Add(layout);

        _refresh.Click += (_, _) => LoadAdapters();
        _browseFile.Click += (_, _) => Browse(_file, "Capture files (*.pcap;*.pcapng)|*.pcap;*.pcapng|All files|*.*");
        _browseOut.Click += (_, _) => BrowseFolder(_out);
        _browseEq.Click += (_, _) => BrowseFolder(_eq);
        _start.Click += (_, _) => Start();
        _stop.Click += (_, _) => Stop();
        _saveAa.Click += (_, _) => SaveAa();
        _openOut.Click += (_, _) => OpenOutput();
        _testUpload.Click += async (_, _) => await TestUploadAsync();
        _uploadSaved.Click += async (_, _) => await UploadSavedAsync();
        _liveMode.CheckedChanged += (_, _) => UpdateModeEnabled();
        _timer.Tick += (_, _) => RefreshStats();
        FormClosing += (_, _) =>
        {
            SaveSettings();
            Stop();
            try { _fileTask?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }   // let a cancelled file run release its files
            try { _uploadStopTask?.Wait(TimeSpan.FromSeconds(20)); } catch (AggregateException) { }   // let the last items reach the website
        };

        Shown += (_, _) =>
        {
            LoadAdapters();
            UpdateModeEnabled();
            Log(Adapters.NpcapInstalled ? "Npcap found." : "Npcap is not installed -- live capture needs it (https://npcap.com). Capture files work without it.");
            if (!IsAdmin()) Log("Not running as administrator -- live capture needs it (Start will offer to restart).");
        };
        _timer.Start();
    }

    // ---- layout helpers ---------------------------------------------------------------------------------------------

    private static GroupBox Section(string title, params Control[] rows)
    {
        var box = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        foreach (var r in rows) flow.Controls.Add(r);
        box.Controls.Add(flow);
        return box;
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        foreach (var c in controls) row.Controls.Add(c);
        return row;
    }

    private static void Browse(TextBox box, string filter)
    {
        using var dialog = new OpenFileDialog { Filter = filter, FileName = box.Text };
        if (dialog.ShowDialog() == DialogResult.OK) box.Text = dialog.FileName;
    }

    private static void BrowseFolder(TextBox box)
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = box.Text };
        if (dialog.ShowDialog() == DialogResult.OK) box.Text = dialog.SelectedPath;
    }

    // ---- state ------------------------------------------------------------------------------------------------------

    private bool Busy => _capture != null || _fileRunning;

    private void UpdateModeEnabled()
    {
        bool live = _liveMode.Checked;
        _adapter.Enabled = live && !Busy;
        _refresh.Enabled = live && !Busy;
        _file.Enabled = !live && !Busy;
        _browseFile.Enabled = !live && !Busy;
        _liveMode.Enabled = _fileMode.Enabled = !Busy;
        _out.Enabled = _eq.Enabled = _browseOut.Enabled = _browseEq.Enabled = _saveRaw.Enabled = !Busy;
        _upload.Enabled = _server.Enabled = _token.Enabled = !Busy;
        _testUpload.Enabled = _uploadSaved.Enabled = !_uploadBusy;
        _start.Enabled = !Busy;
        _stop.Enabled = Busy;
        _start.Text = live ? "Start" : "Process";
        _saveAa.Enabled = _engine != null;
    }

    private void LoadAdapters()
    {
        try
        {
            _adapters = Adapters.List();
        }
        catch (Exception ex)
        {
            _adapters = new List<AdapterInfo>();
            Log("Could not list network adapters: " + ex.Message);
        }
        _adapter.Items.Clear();
        foreach (var a in _adapters) _adapter.Items.Add(a);
        int saved = _adapters.FindIndex(a => a.Display == _settings.Adapter);
        int index = saved >= 0 ? saved : Adapters.DefaultIndex(_adapters);
        if (index >= 0) _adapter.SelectedIndex = index;
    }

    private static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void SaveSettings()
    {
        _settings.OutputFolder = _out.Text.Trim();
        _settings.EqFolder = _eq.Text.Trim();
        _settings.CaptureFile = _file.Text.Trim();
        _settings.SaveRawCapture = _saveRaw.Checked;
        _settings.UploadEnabled = _upload.Checked;
        _settings.UploadServer = _server.Text.Trim();
        _settings.UploadToken = _token.Text;
        if (_adapter.SelectedItem is AdapterInfo a) _settings.Adapter = a.Display;
        _settings.Save();
    }

    private ClientStrings LoadStrings()
    {
        var folder = _eq.Text.Trim();
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            Log("No EverQuest folder set: AAs will be saved without titles and descriptions.");
            return ClientStrings.Empty;
        }
        var s = ClientStrings.LoadFolder(folder);
        foreach (var error in s.LoadErrors) Log("Could not read a client file, carrying on without it -- " + error);
        Log($"Client files read: {s.Titles.Count:N0} titles, {s.Descriptions.Count:N0} descriptions, {s.Spells.Count:N0} spells.");
        return s;
    }

    // ---- start / stop -----------------------------------------------------------------------------------------------

    private void Start()
    {
        SaveSettings();
        if (_liveMode.Checked) StartLive();
        else StartFile();
    }

    private void StartLive()
    {
        if (!Adapters.NpcapInstalled)
        {
            MessageBox.Show(this, "Live capture needs Npcap (free, from https://npcap.com). Install it, then try again.\n\nCapture files can be processed without it.", "Npcap not found", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!IsAdmin())
        {
            var ask = MessageBox.Show(this, "Live capture has to run as administrator. Restart EQ Collector as administrator now?", "Administrator needed", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ask == DialogResult.Yes) RestartAsAdmin();
            return;
        }
        if (_adapter.SelectedItem is not AdapterInfo adapter)
        {
            MessageBox.Show(this, "Pick the network adapter you play on.", "No adapter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            var engine = NewEngine();
            engine.AutoSaveAa = true;       // live: write aa.jsonl as ranks arrive, not only on Stop
            if (_upload.Checked && !StartUploader(engine))
            {
                engine.Close();
                return;
            }
            lock (_engineLock) { _engine = engine; _engineClosed = false; }
            _capture = new LiveCapture(adapter.Device, _settings.Filter, udp => { lock (_engineLock) _engine?.Feed(udp); });
            _capture.Failed += ex => Log("Capture error: " + ex.Message);
            _liveStarted = DateTime.Now;
            _sessionHintShown = false;
            _rawFolder = _out.Text.Trim();
            _rawFailed = false;
            if (_saveRaw.Checked) _capture.Frame += SaveRawFrame;
            _capture.Start();
            _state.Text = "Capturing -- zone, look at items, buy or reset AAs.";
            Log($"Capturing on {adapter.Display}. Filter: {_settings.Filter}");
        }
        catch (Exception ex)
        {
            Log("Could not start capture: " + ex.Message);
            _capture?.Dispose();
            _capture = null;
            DiscardUploader();
            lock (_engineLock)
            {
                _engine?.Close();            // an engine that never ran must not stay selected ("Save AAs now" would use it)
                _engine = null;
                _engineClosed = true;
            }
        }
        UpdateModeEnabled();
    }

    private void StartFile()
    {
        var path = _file.Text.Trim();
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "Pick a capture file (.pcap or .pcapng) first.", "No file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var strings = LoadStrings();
        var outDir = _out.Text.Trim();
        _cancel = new CancellationTokenSource();
        lock (_engineLock) { _engine = null; _engineClosed = false; }   // so the statistics line shows this run, not an earlier session's engine
        _fileRunning = true;
        Volatile.Write(ref _fileProgress, 0);
        _state.Text = "Reading the capture...";
        Log("Reading " + path);
        UpdateModeEnabled();

        bool uploadAfter = _upload.Checked;
        string upServer = _server.Text, upToken = _token.Text;     // read here: the worker must not touch controls
        var token = _cancel.Token;
        _fileTask = Task.Run(() =>
        {
            try
            {
                var engine = CollectorEngine.ProcessFile(path, outDir, strings, Log, n => Volatile.Write(ref _fileProgress, n), token);
                lock (_engineLock) { _engine = engine; _engineClosed = true; }   // ProcessFile already flushed and closed it
                Log($"Done: {engine.Packets:N0} packets, {engine.ItemCount:N0} items, {engine.AaCount:N0} AA ranks. Saved to {outDir}");
                if (uploadAfter) UploadFolderQuietly(outDir, upServer, upToken, token);
            }
            catch (OperationCanceledException)
            {
                Log("Cancelled.");
            }
            catch (Exception ex)
            {
                Log("Failed: " + ex.Message);
            }
            finally
            {
                _fileRunning = false;
                if (IsHandleCreated && !IsDisposed)
                {
                    try { BeginInvoke(() => { _state.Text = "Ready."; UpdateModeEnabled(); RefreshStats(); }); } catch (InvalidOperationException) { }   // the window was closed meanwhile
                }
            }
        });
    }

    /// <summary>On the file worker thread: sends the finished run's items and AA ranks (the whole folder; what was sent before is skipped).</summary>
    private void UploadFolderQuietly(string folder, string server, string tokenText, CancellationToken cancel)
    {
        try
        {
            using var uploader = new CollectorUploader(server, tokenText, folder, Log);
            var r = uploader.UploadFolderAsync(folder, cancel).GetAwaiter().GetResult();
            Log($"Uploaded: {r.ItemsAlreadyOnSite:N0} items were already on the website; {r.ItemsAdded:N0} new items added, {r.ItemsQueued:N0} items and {r.AaQueued:N0} AA ranks queued for review, {r.AaSaved:N0} AA ranks saved.");
        }
        catch (OperationCanceledException)
        {
            Log("Upload cancelled; what is saved goes up next time (or press \"Upload saved files now\").");
        }
        catch (Exception ex) when (ex is UploadAuthException or UploadUnavailableException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Log("Upload: " + ex.Message);
        }
    }

    private CollectorEngine NewEngine()
    {
        var engine = new CollectorEngine(_out.Text.Trim(), LoadStrings(), Log);
        engine.ItemFound += item => Log($"Item: {item.Name} ({item.Id})");
        engine.AaFound += aa => { if (aa.Rank == 1) Log($"AA: id {aa.AaId} (rank 1 of {aa.MaxRank})"); };
        return engine;
    }

    private void Stop()
    {
        _cancel?.Cancel();
        if (_capture != null)
        {
            try { _capture.Dispose(); } catch (Exception ex) { Log("While stopping: " + ex.Message); }
            CloseRaw();
            _capture = null;
        }
        lock (_engineLock)
        {
            if (_engine != null && !_fileRunning && !_engineClosed)
            {
                try
                {
                    int n = _engine.FlushAa();
                    Log($"Stopped. {_engine.ItemCount:N0} items, {n:N0} AA ranks saved to {_engine.OutputFolder}");
                }
                finally
                {
                    _engine.Close();          // whatever happened above, release items.jsonl
                    _engineClosed = true;
                }
            }
        }
        StopUploader();
        _state.Text = "Ready.";
        UpdateModeEnabled();
    }

    private void SaveAa()
    {
        lock (_engineLock)
        {
            if (_engine == null) return;
            int n = _engine.FlushAa();     // a write problem is logged by the engine and gives 0
            if (n > 0) Log($"Saved {n:N0} AA ranks to {Path.Combine(_engine.OutputFolder, "aa.jsonl")}");
        }
    }

    private void OpenOutput()
    {
        try
        {
            var dir = _out.Text.Trim();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
        {
            Log("Cannot open the output folder: " + ex.Message);
        }
    }

    private void RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // the user said no at the UAC prompt
        }
    }

    private void SaveRawFrame(int linkType, double time, byte[] data)
    {
        if (_rawFailed) return;                                  // already reported; do not retry for every packet
        lock (_rawLock)
        {
            try
            {
                if (_raw == null)
                {
                    Directory.CreateDirectory(_rawFolder);
                    _raw = new PcapWriter(Path.Combine(_rawFolder, $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..4]}.pcap"), linkType);
                    Log("Saving the raw traffic to " + _raw.Path);
                }
                _raw.Write(time, data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _rawFailed = true;
                Log("Cannot save the raw capture: " + ex.Message + ". Raw saving is switched off for this run; decoding carries on.");
                try { _raw?.Dispose(); } catch (IOException) { }
                _raw = null;
            }
        }
    }

    private void CloseRaw()
    {
        lock (_rawLock)
        {
            if (_raw == null) return;
            var path = _raw.Path;
            long frames = _raw.Frames;
            try { _raw.Dispose(); } catch (IOException) { }
            _raw = null;
            Log($"Raw capture saved: {path} ({frames:N0} packets)");
        }
    }

    // ---- website upload ---------------------------------------------------------------------------------------------

    /// <summary>The uploader the window's settings describe, or null after telling the user what is wrong.</summary>
    private CollectorUploader? BuildUploader(bool quietIfNotSet = false)
    {
        try
        {
            if (_token.Text.Trim().Length == 0) throw new ArgumentException("Paste your upload token first (the website's capture-token page).");
            return new CollectorUploader(_server.Text, _token.Text, _out.Text.Trim(), Log);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (!quietIfNotSet) MessageBox.Show(this, ex.Message, "Website upload", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
    }

    /// <summary>Starts the background upload for a live capture. False when the settings are not usable (already explained).</summary>
    private bool StartUploader(CollectorEngine engine)
    {
        DiscardUploader();               // never two loops on one engine
        var uploader = BuildUploader();
        if (uploader == null) return false;
        engine.CollectForUpload = true;
        _liveUploader = uploader;
        string folder = _out.Text.Trim();
        _autoUploader = new AutoUploader(uploader, () => { lock (_engineLock) return engine.TakeUpload(); }, Log,       // its own engine, never a later run's
            ct => uploader.UploadFolderAsync(folder, ct));      // first: whatever earlier runs saved but never got sent
        _autoUploader.Start();
        Log($"Uploading new items and AA ranks to {uploader.Server}.");
        return true;
    }

    /// <summary>Drops a background upload without a final round (the capture never started).</summary>
    private void DiscardUploader()
    {
        var auto = _autoUploader;
        var uploader = _liveUploader;
        _autoUploader = null;
        _liveUploader = null;
        auto?.Dispose();
        uploader?.Dispose();
    }

    /// <summary>After Stop: one last attempt to send what is waiting, off the UI thread.</summary>
    private void StopUploader()
    {
        var auto = _autoUploader;
        var uploader = _liveUploader;
        _autoUploader = null;
        _liveUploader = null;
        if (auto == null) return;
        _uploadStopTask = Task.Run(async () =>
        {
            try
            {
                bool all = await auto.StopAsync(TimeSpan.FromSeconds(15));
                Log($"Upload finished: {auto.ItemsAdded} new items added, {auto.ItemsQueued} items and {auto.AaQueued} AA ranks queued for review, {auto.AaSaved} AA ranks saved."
                    + (all ? "" : " Some could not be sent; they are saved in the results folder and go up next time (or press \"Upload saved files now\")."));
            }
            catch (Exception ex)
            {
                Log("Upload: " + ex.Message);
            }
            finally
            {
                auto.Dispose();
                uploader?.Dispose();
            }
        });
    }

    private async Task TestUploadAsync()
    {
        using var uploader = BuildUploader();
        if (uploader == null) return;
        SetUploadBusy(true);
        try
        {
            var ping = await uploader.PingAsync();
            SiteManifest? site = null;
            try { site = await uploader.RefreshManifestAsync(); }
            catch (UploadUnavailableException ex) { Log("Connected, but the website's list could not be pulled (" + ex.Message + "); uploads will ask the site about each item instead."); }
            Log($"Connected to {ping.Site}. " + (ping.Trusted
                ? "Your token adds new items and AA ranks directly."
                : "Everything you upload goes into the website's review queue and is added only after a person approves it."));
            if (site != null) Log($"The website has {site.ItemCount:N0} items and {site.AaCount:N0} AA ranks; only what is not there will be uploaded.");
            MessageBox.Show(this, ping.Trusted ? "Connected. Your token adds new items and AA ranks directly." : "Connected. Everything you upload goes into the website's review queue and is added only after a person approves it.", "Website upload", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is UploadAuthException or UploadUnavailableException or ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            Log("Upload test: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Website upload", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { SetUploadBusy(false); }
    }

    private async Task UploadSavedAsync()
    {
        using var uploader = BuildUploader();
        if (uploader == null) return;
        var folder = _out.Text.Trim();
        SetUploadBusy(true);
        try
        {
            Log($"Uploading what is saved in {folder}...");
            var r = await Task.Run(() => uploader.UploadFolderAsync(folder));
            Log($"Done: {r.ItemsAlreadyOnSite:N0} items already on the website, {r.ItemsChecked:N0} checked as possibly new, {r.ItemsAdded:N0} added, {r.ItemsQueued:N0} items and {r.AaQueued:N0} AA ranks queued for review, {r.AaSaved:N0} AA ranks saved.");
        }
        catch (Exception ex) when (ex is UploadAuthException or UploadUnavailableException or ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            Log("Upload: " + ex.Message);
        }
        finally { SetUploadBusy(false); }
    }

    private void SetUploadBusy(bool busy)
    {
        _uploadBusy = busy;
        UpdateModeEnabled();
    }

    // ---- display ----------------------------------------------------------------------------------------------------

    private void RefreshStats()
    {
        lock (_engineLock)
        {
            if (_engine != null)
            {
                if (_capture != null && !_engineClosed) _engine.MaybeAutoSaveAa();     // ranks that arrived at the end of a burst
                if (_autoUploader is { Active: false } && _engine.CollectForUpload)
                {
                    _engine.CollectForUpload = false;                                  // the website refused the token: stop keeping copies of every item
                    _engine.TakeUpload();
                }
                _stats.Text = $"Packets {_engine.Packets:N0}  |  Sessions {_engine.Sessions:N0}  |  Game packets {_engine.AppPackets:N0}  |  Items {_engine.ItemCount:N0}  |  AA ranks {_engine.AaCount:N0}  |  AA type {(_engine.AaOpcode is { } op ? "0x" + op.ToString("x") : "not seen")}" + UploadStats();
                if (_capture != null && !_sessionHintShown && _engine.Packets > 0 && _engine.Sessions == 0 && DateTime.Now - _liveStarted > TimeSpan.FromSeconds(30))
                {
                    _sessionHintShown = true;
                    Log("Traffic is arriving but no game session has been seen. A session is recognised when it is set up, so zone to a new area (or log in) while capturing.");
                }
            }
            else if (_fileRunning)
            {
                _stats.Text = $"Reading... {Volatile.Read(ref _fileProgress),12:N0} packets";
            }
        }
    }

    private string UploadStats()
    {
        if (_autoUploader is not { } up) return "";
        if (!up.Active) return "  |  Upload stopped";
        string newToSite = _liveUploader?.Manifest is { } m && _engine != null ? $"New to the site {_engine.Items.Count(i => !m.HasItem(i.Id)):N0}  |  " : "";
        return $"  |  {newToSite}Sent {up.ItemsAdded + up.ItemsQueued:N0} items, {up.AaSaved + up.AaQueued:N0} AA";
    }

    private void Log(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke(() => Log(message)); } catch (InvalidOperationException) { }     // the window is closing
            return;
        }
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        if (_log.Lines.Length > MaxLogLines) _log.Lines = _log.Lines.Skip(_log.Lines.Length - MaxLogLines / 2).ToArray();
    }
}
