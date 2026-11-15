using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using PSToolKit.Models;
using PSToolKit.Services;

namespace PSToolKit;

/// <summary>
/// Main window. Built in code (no .Designer.cs) so the whole layout is readable in one place.
/// Left: searchable command tree. Right: description, parameter inputs, buttons, live output.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly string CatalogPath = Path.Combine(AppContext.BaseDirectory, "commands.json");
    private static readonly string WindowSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PSToolKit",
        "windowsettings.json");

    // Output colors (dark console look)
    private static readonly Color OutputBack = Color.FromArgb(24, 24, 28);
    private static readonly Color OutputText = Color.FromArgb(220, 220, 220);
    private static readonly Color ErrorText = Color.FromArgb(255, 120, 110);
    private static readonly Color HeaderText = Color.FromArgb(110, 170, 255);
    private static readonly Color SuccessText = Color.FromArgb(120, 210, 140);

    private readonly PowerShellRunner _runner = new();
    private readonly ConcurrentQueue<(string Text, Color Color)> _pendingOutput = new();
    private readonly System.Windows.Forms.Timer _flushTimer = new() { Interval = 100 };
    private readonly Dictionary<string, string> _rememberedValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Control> _parameterInputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _runClock = new();

    private CommandCatalog _catalog = new();
    private CommandDefinition? _selected;
    private CancellationTokenSource? _runCts;

    // Controls
    private readonly TextBox _search = new();
    private readonly TreeView _tree = new();
    private readonly Label _title = new();
    private readonly Label _description = new();
    private readonly Label _requires = new();
    private readonly TableLayoutPanel _paramGrid = new();
    private readonly GroupBox _paramGroup = new();
    private readonly Button _runButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _scriptButton = new();
    private readonly Button _copyButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _clearButton = new();
    private readonly RichTextBox _output = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripStatusLabel _elapsed = new();
    private readonly ToolTip _tips = new();

    public MainForm()
    {
        Text = "PSToolKit" + (IsElevated() ? "  (Administrator)" : "");
        Size = new Size(1400, 900);
        MinimumSize = new Size(1100, 700);
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        DpiChanged += (_, _) =>
        {
            _tree.ItemHeight = Math.Max(32, (int)(32 * DeviceDpi / 96f));
        };

        BuildLayout();
        WireEvents();
        LoadWindowSettings();
        LoadCatalog();
    }

    // ───────────────────────────── Layout ─────────────────────────────

    private void BuildLayout()
    {
        // Menu
        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("&File");
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("&Reload commands", null, (_, _) => LoadCatalog())
        {
            ShortcutKeys = Keys.Control | Keys.R
        });
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("&Edit commands.json…", null, (_, _) => OpenCatalogInEditor()));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add("E&xit", null, (_, _) => Close());
        menu.Items.Add(fileMenu);
        MainMenuStrip = menu;

        // Status bar
        var statusStrip = new StatusStrip();
        _status.Spring = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_elapsed);

        // Split: tree | details
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 6
        };

        // Left side
        _search.Dock = DockStyle.Top;
        _search.PlaceholderText = "Search commands…";
        _tree.Dock = DockStyle.Fill;
        _tree.HideSelection = false;
        _tree.FullRowSelect = true;
        _tree.ShowLines = false;
        _tree.ItemHeight = Math.Max(32, (int)(32 * DeviceDpi / 96f));
        _tree.Indent = 22;
        _tree.ShowNodeToolTips = true;
        var leftPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        leftPanel.Controls.Add(_tree);
        leftPanel.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 }); // spacer
        leftPanel.Controls.Add(_search);
        split.Panel1.Controls.Add(leftPanel);

        // Right side
        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(8)
        };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // title
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // description
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // requires
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // parameters
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // output

        _title.AutoSize = true;
        _title.Font = new Font("Segoe UI Semibold", 14f);
        _title.Margin = new Padding(0, 0, 0, 4);

        _description.AutoSize = true;
        _description.MaximumSize = new Size(1200, 0);
        _description.Margin = new Padding(0, 0, 0, 4);

        _requires.AutoSize = true;
        _requires.ForeColor = Color.DarkOrange;
        _requires.Margin = new Padding(0, 0, 0, 6);

        _paramGrid.Dock = DockStyle.Top;
        _paramGrid.AutoSize = true;
        _paramGrid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _paramGrid.ColumnCount = 2;
        _paramGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _paramGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _paramGroup.Text = "Parameters";
        _paramGroup.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        _paramGroup.AutoSize = true;
        _paramGroup.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _paramGroup.Padding = new Padding(8);
        _paramGroup.Controls.Add(_paramGrid);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 6, 0, 6)
        };
        ConfigureButton(_runButton, "▶  Run  (F5)", 110);
        _runButton.BackColor = Color.FromArgb(0, 120, 212);
        _runButton.ForeColor = Color.White;
        _runButton.FlatStyle = FlatStyle.Flat;
        _runButton.FlatAppearance.BorderSize = 0;
        ConfigureButton(_stopButton, "■  Stop", 80);
        ConfigureButton(_scriptButton, "Show script", 100);
        ConfigureButton(_clearButton, "Clear", 70);
        ConfigureButton(_copyButton, "Copy output", 100);
        ConfigureButton(_saveButton, "Save output…", 105);
        _stopButton.Enabled = false;
        buttons.Controls.AddRange(new Control[] { _runButton, _stopButton, _scriptButton, _clearButton, _copyButton, _saveButton });

        _output.Dock = DockStyle.Fill;
        _output.ReadOnly = true;
        _output.BackColor = OutputBack;
        _output.ForeColor = OutputText;
        _output.Font = new Font("Consolas", 10f);
        _output.WordWrap = false;
        _output.BorderStyle = BorderStyle.None;
        _output.DetectUrls = false;
        _output.HideSelection = false;

        right.Controls.Add(_title, 0, 0);
        right.Controls.Add(_description, 0, 1);
        right.Controls.Add(_requires, 0, 2);
        right.Controls.Add(_paramGroup, 0, 3);
        right.Controls.Add(buttons, 0, 4);
        right.Controls.Add(_output, 0, 5);
        split.Panel2.Controls.Add(right);

        Controls.Add(split);
        Controls.Add(statusStrip);
        Controls.Add(menu);

        // SplitterDistance must be set after the control has a size.
        Load += (_, _) =>
        {
            int preferredWidth = (int)(split.ClientSize.Width * 0.33);
            split.SplitterDistance = Math.Clamp(preferredWidth, 400, 550);
        };
    }

    private void ConfigureButton(Button b, string text, int width)
    {
        float scale = DeviceDpi / 96f;

        b.Text = text;
        b.Width = (int)(width * scale);
        b.Height = (int)(40 * scale);
        b.Margin = new Padding(0, 0, 6, 0);
        b.AutoSize = false;
        b.UseCompatibleTextRendering = true;
    }

    private void WireEvents()
    {
        _search.TextChanged += (_, _) => PopulateTree(_search.Text);
        _tree.AfterSelect += (_, e) => SelectCommand(e.Node?.Tag as CommandDefinition);
        _tree.NodeMouseDoubleClick += async (_, e) => { if (e.Node?.Tag is CommandDefinition) await RunSelectedAsync(); };
        _runButton.Click += async (_, _) => await RunSelectedAsync();
        _stopButton.Click += (_, _) => _runCts?.Cancel();
        _scriptButton.Click += (_, _) => ShowScript();
        _clearButton.Click += (_, _) => _output.Clear();
        _copyButton.Click += (_, _) => { if (_output.TextLength > 0) Clipboard.SetText(_output.Text); SetStatus("Output copied to clipboard."); };
        _saveButton.Click += (_, _) => SaveOutput();

        _runner.OutputReceived += (line, isError) => _pendingOutput.Enqueue((line + Environment.NewLine, isError ? ErrorText : OutputText));
        _flushTimer.Tick += (_, _) => FlushOutput();
        _flushTimer.Start();

        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.F5 && _runButton.Enabled) { e.Handled = true; await RunSelectedAsync(); }
            else if (e.KeyCode == Keys.Escape && _stopButton.Enabled) { e.Handled = true; _runCts?.Cancel(); }
            else if (e.Control && e.KeyCode == Keys.F) { e.Handled = true; _search.Focus(); _search.SelectAll(); }
        };

        FormClosing += (_, _) =>
        {
            _runCts?.Cancel();
            SaveWindowSettings();
        };
    }

    // ───────────────────────────── Catalog / tree ─────────────────────────────

    private void LoadCatalog()
    {
        try
        {
            _catalog = CommandCatalog.Load(CatalogPath);
            PopulateTree(_search.Text);
            SetStatus($"Loaded {_catalog.Commands.Count} commands from commands.json.");
        }
        catch (Exception ex)
        {
            _catalog = new CommandCatalog();
            PopulateTree("");
            MessageBox.Show(this, $"Couldn't load commands.json:\n\n{ex.Message}\n\nPath: {CatalogPath}",
                "Command catalog error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("commands.json failed to load.");
        }
    }

    private void PopulateTree(string filter)
    {
        var selectedName = _selected?.Name;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();

        var matches = _catalog.Commands.Where(c =>
            string.IsNullOrWhiteSpace(filter) ||
            c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            c.Description.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            c.Category.Contains(filter, StringComparison.OrdinalIgnoreCase));

        TreeNode? toSelect = null;
        foreach (var group in matches.GroupBy(c => c.Category))
        {
            // (A bold NodeFont gets clipped in WinForms trees, so categories are uppercase + gray instead.)
            var catNode = new TreeNode(group.Key.ToUpperInvariant()) { ForeColor = Color.DimGray };
            foreach (var cmd in group)
            {
                var node = new TreeNode(cmd.Confirm ? cmd.Name + "  ⚠" : cmd.Name) { Tag = cmd, ToolTipText = cmd.Description };
                catNode.Nodes.Add(node);
                if (cmd.Name == selectedName) toSelect = node;
            }
            _tree.Nodes.Add(catNode);
        }

        _tree.ExpandAll();
        _tree.EndUpdate();
        _tree.ShowNodeToolTips = true;

        if (toSelect != null) _tree.SelectedNode = toSelect;
        else if (_selected == null) SelectCommand(null);
    }

    private void SelectCommand(CommandDefinition? cmd)
    {
        SaveCurrentValues();
        _selected = cmd;
        _parameterInputs.Clear();

        _paramGrid.SuspendLayout();
        var oldInputs = _paramGrid.Controls.Cast<Control>().ToList();
        _paramGrid.Controls.Clear();
        foreach (var c in oldInputs) c.Dispose();
        _paramGrid.RowStyles.Clear();
        _paramGrid.RowCount = 0;

        if (cmd == null)
        {
            _title.Text = "Select a command";
            _description.Text = "Pick a command from the list on the left. Commands marked ⚠ change something and ask before they run.";
            _requires.Text = "";
            _paramGroup.Visible = false;
            _runButton.Enabled = _scriptButton.Enabled = false;
            _paramGrid.ResumeLayout();
            return;
        }

        _title.Text = cmd.Name;
        _description.Text = cmd.Description;
        _requires.Text = string.IsNullOrWhiteSpace(cmd.Requires) ? "" : "Requires: " + cmd.Requires;
        _runButton.Enabled = _runCts == null;
        _scriptButton.Enabled = true;
        _paramGroup.Visible = cmd.Parameters.Count > 0;

        foreach (var p in cmd.Parameters)
        {
            var label = new Label
            {
                Text = p.DisplayLabel + (p.Required ? " *" : ""),
                AutoSize = true,
                MaximumSize = new Size(250, 0),
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 12, 6)
            };

            string? remembered = null;
            if (p.Kind == ParameterKind.Text) _rememberedValues.TryGetValue(p.Name, out remembered);
            var initial = remembered ?? p.Default ?? "";
            Control input = CreateInput(p, initial);
            input.Margin = new Padding(0, 3, 0, 3);
            if (!string.IsNullOrWhiteSpace(p.Hint)) { _tips.SetToolTip(input, p.Hint); _tips.SetToolTip(label, p.Hint); }

            // Enter in any field runs the command.
            input.KeyDown += async (_, e) =>
            {
                if (e.KeyCode == Keys.Enter && _runButton.Enabled) { e.SuppressKeyPress = true; await RunSelectedAsync(); }
            };

            _paramGrid.RowCount++;
            _paramGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _paramGrid.Controls.Add(label, 0, _paramGrid.RowCount - 1);
            _paramGrid.Controls.Add(input, 1, _paramGrid.RowCount - 1);
            _parameterInputs[p.Name] = input;
        }

        _paramGrid.ResumeLayout();
    }

    private static Control CreateInput(CommandParameter p, string initial)
    {
        switch (p.Kind)
        {
            case ParameterKind.Choice:
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
                combo.Items.AddRange(p.Options.Cast<object>().ToArray());
                var idx = p.Options.FindIndex(o => o.Equals(initial, StringComparison.OrdinalIgnoreCase));
                combo.SelectedIndex = idx >= 0 ? idx : (combo.Items.Count > 0 ? 0 : -1);
                return combo;

            case ParameterKind.Switch:
                return new CheckBox
                {
                    Checked = initial.Equals("true", StringComparison.OrdinalIgnoreCase),
                    AutoSize = true,
                    Text = p.Hint ?? ""
                };

            case ParameterKind.Number:
                var num = new NumericUpDown { Minimum = 0, Maximum = 1_000_000, Width = 120 };
                if (decimal.TryParse(initial, out var n)) num.Value = Math.Clamp(n, num.Minimum, num.Maximum);
                return num;

            default:
                return new TextBox
                {
                    Text = initial,
                    PlaceholderText = p.Hint ?? "",
                    Dock = DockStyle.Fill,
                    MinimumSize = new Size(260, 0)
                };
        }
    }

    private Dictionary<string, string> ReadValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, control) in _parameterInputs)
        {
            values[name] = control switch
            {
                ComboBox cb => cb.SelectedItem?.ToString() ?? "",
                CheckBox chk => chk.Checked ? "true" : "false",
                NumericUpDown nud => ((int)nud.Value).ToString(),
                _ => control.Text
            };
        }
        return values;
    }

    /// <summary>Carry typed values (e.g. ComputerName, User) over to other commands that use the same name.</summary>
    private void SaveCurrentValues()
    {
        // Only text fields are shared between commands. Number/Choice/Switch parameters reuse names
        // (Count, Type, Action...) with different meanings and defaults, so remembering them would
        // override each command's own default.
        var values = ReadValues();
        foreach (var p in _selected?.Parameters ?? new List<CommandParameter>())
        {
            if (p.Kind == ParameterKind.Text && values.TryGetValue(p.Name, out var value))
                _rememberedValues[p.Name] = value;
        }
    }

    // ───────────────────────────── Running ─────────────────────────────

    private async Task RunSelectedAsync()
    {
        var cmd = _selected;
        if (cmd == null || _runCts != null) return;

        var values = ReadValues();
        var missing = cmd.Parameters.Where(p => p.Required && string.IsNullOrWhiteSpace(values.GetValueOrDefault(p.Name)))
                                    .Select(p => p.DisplayLabel).ToList();
        if (missing.Count > 0)
        {
            MessageBox.Show(this, "Please fill in: " + string.Join(", ", missing), "Missing input",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _parameterInputs[cmd.Parameters.First(p => p.DisplayLabel == missing[0]).Name].Focus();
            return;
        }

        if (cmd.Confirm)
        {
            var summary = string.Join(Environment.NewLine,
                cmd.Parameters.Select(p => $"   {p.DisplayLabel}: {values.GetValueOrDefault(p.Name)}"));
            var answer = MessageBox.Show(this,
                $"This will make a change:\n\n{cmd.Name}\n{summary}\n\nContinue?",
                "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }

        SaveCurrentValues();
        var script = PowerShellRunner.BuildUserScript(cmd, values);

        var paramText = string.Join("  ", cmd.Parameters
            .Where(p => !string.IsNullOrWhiteSpace(values.GetValueOrDefault(p.Name)))
            .Select(p => $"{p.Name}={values[p.Name]}"));
        if (_output.TextLength > 0) _pendingOutput.Enqueue((Environment.NewLine, OutputText));
        _pendingOutput.Enqueue(($"▶ {cmd.Name}   {paramText}   [{DateTime.Now:HH:mm:ss}]{Environment.NewLine}", HeaderText));

        var cts = new CancellationTokenSource();
        _runCts = cts;
        SetRunning(true);
        SetStatus($"Running: {cmd.Name}…");
        _runClock.Restart();

        int exitCode;
        try
        {
            exitCode = await _runner.RunAsync(script, cts.Token);
        }
        catch (Exception ex)
        {
            _pendingOutput.Enqueue(("Failed to start PowerShell: " + ex.Message + Environment.NewLine, ErrorText));
            exitCode = 1;
        }
        finally
        {
            _runClock.Stop();
            _runCts = null;
            cts.Dispose();
            SetRunning(false);
        }

        var secs = _runClock.Elapsed.TotalSeconds.ToString("0.0");
        var (msg, color) = exitCode switch
        {
            0 => ($"■ Completed in {secs}s", SuccessText),
            -1 => ($"■ Stopped after {secs}s", ErrorText),
            _ => ($"■ Finished with errors in {secs}s", ErrorText)
        };
        _pendingOutput.Enqueue((msg + Environment.NewLine, color));
        SetStatus(msg.TrimStart('■', ' '));
    }

    private void SetRunning(bool running)
    {
        _runButton.Enabled = !running && _selected != null;
        _stopButton.Enabled = running;
        _tree.Enabled = !running;
        _search.Enabled = !running;
        _paramGroup.Enabled = !running;
    }

    private void FlushOutput()
    {
        if (_runCts != null) _elapsed.Text = _runClock.Elapsed.ToString(@"mm\:ss");
        if (_pendingOutput.IsEmpty) return;

        _output.SuspendLayout();
        int count = 0;
        while (count < 2000 && _pendingOutput.TryDequeue(out var item))
        {
            _output.SelectionStart = _output.TextLength;
            _output.SelectionLength = 0;
            _output.SelectionColor = item.Color;
            _output.AppendText(item.Text);
            count++;
        }
        _output.SelectionColor = OutputText;
        _output.ScrollToCaret();
        _output.ResumeLayout();
    }

    // ───────────────────────────── Buttons / helpers ─────────────────────────────

    private void ShowScript()
    {
        if (_selected == null) return;
        var script = PowerShellRunner.BuildUserScript(_selected, ReadValues());

        using var dlg = new Form
        {
            Text = "Script — " + _selected.Name,
            Size = new Size(820, 520),
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            ShowInTaskbar = false
        };
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 10f),
            Text = script.Replace("\r\n", "\n").Replace("\n", "\r\n")
        };
        var copy = new Button { Text = "Copy to clipboard", Dock = DockStyle.Bottom, Height = 34 };
        copy.Click += (_, _) => { Clipboard.SetText(script); copy.Text = "Copied"; };
        dlg.Controls.Add(box);
        dlg.Controls.Add(copy);
        dlg.ShowDialog(this);
    }

    private void SaveOutput()
    {
        if (_output.TextLength == 0) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"{(_selected?.Name ?? "output").Replace(' ', '-')}-{DateTime.Now:yyyyMMdd-HHmm}.txt"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            File.WriteAllText(dlg.FileName, _output.Text);
            SetStatus("Saved to " + dlg.FileName);
        }
    }

    private void OpenCatalogInEditor()
    {
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{CatalogPath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't open editor"); }
    }

    private void LoadWindowSettings()
    {
        try
        {
            // Fall back to the settings saved under the app's old name (PowerShellToolkit).
            var path = WindowSettingsPath;
            if (!File.Exists(path))
                path = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(WindowSettingsPath)!)!,
                    "PowerShellToolkit", "windowsettings.json");
            if (!File.Exists(path))
                return;

            var settings = JsonSerializer.Deserialize<WindowSettings>(
                File.ReadAllText(path));

            if (settings == null)
                return;

            var savedBounds = new Rectangle(settings.Left, settings.Top,
                Math.Max(settings.Width, MinimumSize.Width),
                Math.Max(settings.Height, MinimumSize.Height));

            bool visibleOnScreen = Screen.AllScreens.Any(screen =>
                screen.WorkingArea.IntersectsWith(savedBounds));

            if (visibleOnScreen)
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = savedBounds;
            }

            WindowState = settings.WindowState == FormWindowState.Minimized
                ? FormWindowState.Normal
                : settings.WindowState;
        }
        catch
        {
            // Use the default maximized window if settings cannot be loaded.
        }
    }

    private void SaveWindowSettings()
    {
        try
        {
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var settings = new WindowSettings
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                WindowState = WindowState == FormWindowState.Minimized
                    ? FormWindowState.Normal
                    : WindowState
            };

            Directory.CreateDirectory(Path.GetDirectoryName(WindowSettingsPath)!);
            File.WriteAllText(WindowSettingsPath, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Do not block application shutdown if settings cannot be saved.
        }
    }

    private sealed class WindowSettings
    {
        public int Width { get; set; } = 1400;
        public int Height { get; set; } = 900;
        public int Left { get; set; } = 100;
        public int Top { get; set; } = 100;
        public FormWindowState WindowState { get; set; } = FormWindowState.Maximized;
    }

    private void SetStatus(string text) => _status.Text = text;

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _flushTimer.Dispose();
            _tips.Dispose();
            _runCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
