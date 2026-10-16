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
    private readonly Label _state = new() { AutoSize = true, Text = "Ready." };
    private readonly Label _stats = new() { AutoSize = true, Font = new Font("Consolas", 10f), Text = "" };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    private List<AdapterInfo> _adapters = new();
    private CollectorEngine? _engine;
    private LiveCapture? _capture;
    private CancellationTokenSource? _cancel;
    private long _fileProgress;                 // written by the file worker, read by the UI timer (Interlocked / Volatile)
    private volatile bool _fileRunning;
    private bool _engineClosed;                 // the engine has been flushed and closed: Stop must not do it again
    private Task? _fileTask;

    public MainForm()
    {
        Text = "EQ Collector " + typeof(MainForm).Assembly.GetName().Version?.ToString(3);
        ClientSize = new Size(760, 640);
        MinimumSize = new Size(700, 560);
        StartPosition = FormStartPosition.CenterScreen;

        _out.Text = _settings.OutputFolder;
        _eq.Text = _settings.EqFolder;
        _file.Text = _settings.CaptureFile;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
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
            new Label { Text = "The EverQuest folder (dbstr_us.txt, spells_us.txt) gives AA titles and descriptions; without it AAs are saved with ids only.", AutoSize = true, ForeColor = SystemColors.GrayText }), 0, 1);

        layout.Controls.Add(Row(_start, _stop, _saveAa, _openOut, _state), 0, 2);
        layout.Controls.Add(_stats, 0, 3);
        layout.Controls.Add(new Label { Text = "Items and AAs are saved as they are found. Nothing is sent anywhere -- the files stay on this computer.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 6) }, 0, 4);
        layout.Controls.Add(_log, 0, 5);
        Controls.Add(layout);

        _refresh.Click += (_, _) => LoadAdapters();
        _browseFile.Click += (_, _) => Browse(_file, "Capture files (*.pcap;*.pcapng)|*.pcap;*.pcapng|All files|*.*");
        _browseOut.Click += (_, _) => BrowseFolder(_out);
        _browseEq.Click += (_, _) => BrowseFolder(_eq);
        _start.Click += (_, _) => Start();
        _stop.Click += (_, _) => Stop();
        _saveAa.Click += (_, _) => SaveAa();
        _openOut.Click += (_, _) => OpenOutput();
        _liveMode.CheckedChanged += (_, _) => UpdateModeEnabled();
        _timer.Tick += (_, _) => RefreshStats();
        FormClosing += (_, _) =>
        {
            SaveSettings();
            Stop();
            try { _fileTask?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }   // let a cancelled file run release its files
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
        _out.Enabled = _eq.Enabled = _browseOut.Enabled = _browseEq.Enabled = !Busy;
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
            lock (_engineLock) { _engine = engine; _engineClosed = false; }
            _capture = new LiveCapture(adapter.Device, _settings.Filter, udp => { lock (_engineLock) _engine?.Feed(udp); });
            _capture.Failed += ex => Log("Capture error: " + ex.Message);
            _capture.Start();
            _state.Text = "Capturing -- zone, look at items, buy or reset AAs.";
            Log($"Capturing on {adapter.Display}. Filter: {_settings.Filter}");
        }
        catch (Exception ex)
        {
            Log("Could not start capture: " + ex.Message);
            _capture?.Dispose();
            _capture = null;
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

        var token = _cancel.Token;
        _fileTask = Task.Run(() =>
        {
            try
            {
                var engine = CollectorEngine.ProcessFile(path, outDir, strings, Log, n => Volatile.Write(ref _fileProgress, n), token);
                lock (_engineLock) { _engine = engine; _engineClosed = true; }   // ProcessFile already flushed and closed it
                Log($"Done: {engine.Packets:N0} packets, {engine.ItemCount:N0} items, {engine.AaCount:N0} AA ranks. Saved to {outDir}");
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

    // ---- display ----------------------------------------------------------------------------------------------------

    private void RefreshStats()
    {
        lock (_engineLock)
        {
            if (_engine != null)
            {
                _stats.Text = $"Packets {_engine.Packets,12:N0}    Items {_engine.ItemCount,8:N0}    AA ranks {_engine.AaCount,6:N0}    AA packet type {(_engine.AaOpcode is { } op ? "0x" + op.ToString("x") : "not seen yet")}";
            }
            else if (_fileRunning)
            {
                _stats.Text = $"Reading... {Volatile.Read(ref _fileProgress),12:N0} packets";
            }
        }
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
