// AutoAccept.cs
// Watches the VS Code window and automatically clicks confirmation buttons
// (like "OK" / "Allow" / "Yes") that appear in the agent chat.
//
// How it works: it uses Windows UI Automation to find buttons in the VS Code
// window and "presses" them via the Invoke pattern (or a background
// PostMessage click as a fallback). The real mouse cursor is never moved
// and the window does not need to be focused.
//
// The app is a normal window: "Включить"/"Выключить" start and stop the
// watcher, the "Hidden mode" checkbox switches between:
//   on (default)  - the VS Code window is NEVER brought to the foreground.
//                   No SetForegroundWindow / SetFocus / BringWindowToTop
//                   calls are made. If clicking still makes VS Code steal
//                   the focus, the previously focused window gets it back.
//   off           - VS Code is activated before the click so the press is
//                   visible, the focus is not restored.
//
// Build:  build.cmd
// Run:    AutoAccept.exe [button names...] [--dry-run]
//         --dry-run  = only log matches, do not click (for testing)
//
// NOTE: VS Code (Electron) exposes its UI to UI Automation only when
// accessibility mode is on. If nothing is detected, start VS Code with:
//     code --force-renderer-accessibility

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

class AutoAccept
{
    // Default button names (or their substrings) to click, case-insensitive.
    static readonly string[] DefaultButtons =
    {
        "OK", "ОК", "Allow", "Разрешить", "Proceed", "Продолжить",
        "Yes", "Да", "Accept", "Принять", "Confirm", "Подтвердить"
    };

    const int PollIntervalMs = 700;   // how often to scan the window
    const int ReClickCooldownMs = 3000; // do not click the same button again too fast

    static string[] _patterns;
    static Regex[] _patternRegexes;
    static bool _dryRun;
    static volatile bool _hiddenMode = true;  // never activate VS Code, restore focus

    // The watcher runs on its own thread so the window stays responsive
    static readonly object _gate = new object();
    static volatile bool _running;
    static bool _waitingLogged;   // "VS Code not found" is logged once, not every scan

    // Set by the window to receive log lines
    internal static Action<string> LogSink;

    // Last clicked button: runtime id hash + timestamp (to avoid double clicks)
    static int _lastClickedId;
    static DateTime _lastClickedAt = DateTime.MinValue;

    #region WinAPI (fallback background click, no cursor movement and no focus change)
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    const uint WM_LBUTTONDOWN = 0x0201;
    const uint WM_LBUTTONUP   = 0x0202;
    #endregion

    // Command line options (button names, --dry-run) are still supported
    internal static void Configure(string[] args)
    {
        _dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        _patterns = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (_patterns.Length == 0) _patterns = DefaultButtons;

        // Whole-word matching (case-insensitive) to avoid false positives,
        // e.g. "Да" must not match inside "Повторить редактирование".
        _patternRegexes = _patterns.Select(p => new Regex(
            @"(^|[^\p{L}\p{Nd}])" + Regex.Escape(p) + @"($|[^\p{L}\p{Nd}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
    }

    internal static string[] Patterns { get { return _patterns; } }
    internal static bool DryRun { get { return _dryRun; } }

    internal static bool HiddenMode
    {
        get { return _hiddenMode; }
        set { _hiddenMode = value; }
    }

    internal static bool Running { get { return _running; } }

    internal static void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _waitingLogged = false;
            var worker = new Thread(PollLoop) { IsBackground = true };
            worker.Start();
        }
    }

    internal static void Stop()
    {
        lock (_gate) { _running = false; }
    }

    static void PollLoop()
    {
        while (_running)
        {
            try { Tick(); }
            catch (Exception ex) { Log("Error: " + ex.Message); }

            Thread.Sleep(PollIntervalMs);
        }
    }

    static void Tick()
    {
        IntPtr hwnd = FindVsCodeWindow();
        if (hwnd == IntPtr.Zero)
        {
            if (!_waitingLogged)
            {
                _waitingLogged = true;
                Log("VS Code window not found, waiting...");
            }
            Thread.Sleep(2000);
            return;
        }

        if (_waitingLogged)
        {
            _waitingLogged = false;
            Log("VS Code window found.");
        }

        var root = AutomationElement.FromHandle(hwnd);

        var buttons = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        for (int i = 0; i < buttons.Count; i++)
        {
            var btn = buttons[i];
            string name;
            try { name = btn.Current.Name; } catch { continue; }

            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!Matches(name)) continue;

            // Skip the same button if it was clicked recently
            int id = GetId(btn);
            if (id == _lastClickedId && (DateTime.UtcNow - _lastClickedAt).TotalMilliseconds < ReClickCooldownMs)
                continue;

            Log("Found button: \"" + name + "\"" + (_dryRun ? " (dry-run, not clicking)" : ""));

            if (!_dryRun)
            {
                // Hidden mode: the window is never activated, so remember what
                // window had the focus now to give it back after the click.
                IntPtr focusBefore = GetForegroundWindow();
                if (!_hiddenMode) SetForeground(hwnd);

                bool pressed = TryPress(btn);
                if (!pressed) PostClick(hwnd, btn);

                _lastClickedId = id;
                _lastClickedAt = DateTime.UtcNow;
                Log((pressed ? "Clicked: " : "Sent click to: ") + "\"" + name + "\"");

                if (_hiddenMode) RestoreFocus(hwnd, focusBefore);
            }
        }
    }

    static IntPtr FindVsCodeWindow()
    {
        var proc = Process.GetProcessesByName("Code")
            .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero && IsWindow(p.MainWindowHandle));
        return proc != null ? proc.MainWindowHandle : IntPtr.Zero;
    }

    static bool Matches(string name)
    {
        foreach (var re in _patternRegexes)
            if (re.IsMatch(name))
                return true;
        return false;
    }

    static int GetId(AutomationElement el)
    {
        try
        {
            int hash = 17;
            foreach (int v in el.GetRuntimeId()) hash = hash * 31 + v;
            return hash;
        }
        catch { return 0; }
    }

    // Background press using UIA patterns only. None of them activates or
    // focuses the window (SetFocus is deliberately never called).
    static bool TryPress(AutomationElement btn)
    {
        return TryInvoke(btn) || TryToggle(btn) || TrySelect(btn);
    }

    // Preferred way: UIA Invoke - works in background, does not touch the mouse
    static bool TryInvoke(AutomationElement btn)
    {
        try
        {
            object pattern;
            if (btn.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
            {
                ((InvokePattern)pattern).Invoke();
                return true;
            }
        }
        catch { }
        return false;
    }

    // Toggle button (checkbox-like "Allow" toggles)
    static bool TryToggle(AutomationElement btn)
    {
        try
        {
            object pattern;
            if (btn.TryGetCurrentPattern(TogglePattern.Pattern, out pattern))
            {
                ((TogglePattern)pattern).Toggle();
                return true;
            }
        }
        catch { }
        return false;
    }

    // Button inside a list/toolbar often supports SelectionItem instead of Invoke
    static bool TrySelect(AutomationElement btn)
    {
        try
        {
            object pattern;
            if (btn.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
            {
                ((SelectionItemPattern)pattern).Select();
                return true;
            }
        }
        catch { }
        return false;
    }

    // Fallback: background mouse click via PostMessage (cursor is not moved)
    static void PostClick(IntPtr hwnd, AutomationElement btn)
    {
        var r = btn.Current.BoundingRectangle;
        if (r.IsEmpty)
        {
            Log("Button has no screen bounds (window minimized/hidden), background click is not possible");
            return;
        }

        var pt = new POINT { X = (int)(r.Left + r.Width / 2), Y = (int)(r.Top + r.Height / 2) };
        ScreenToClient(hwnd, ref pt);
        IntPtr lparam = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));

        PostMessage(hwnd, WM_LBUTTONDOWN, IntPtr.Zero, lparam);
        Thread.Sleep(50);
        PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lparam);
    }

    // If VS Code took the foreground while clicking, hand it back to the
    // window that had it before. Only used in hidden mode.
    static void RestoreFocus(IntPtr vscodeHwnd, IntPtr focusBefore)
    {
        if (focusBefore == IntPtr.Zero || focusBefore == vscodeHwnd) return;

        // Electron may take the focus slightly after the click, so re-check a few times.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetForegroundWindow() == vscodeHwnd)
            {
                if (SetForeground(focusBefore)) Log("Focus returned to the previous window");
                return;
            }
            Thread.Sleep(120);
        }
    }

    // SetForegroundWindow is blocked by Windows for background processes,
    // so the input queues of both threads are attached first.
    static bool SetForeground(IntPtr target)
    {
        try
        {
            if (!IsWindow(target)) return false;

            IntPtr fg = GetForegroundWindow();
            uint current = GetCurrentThreadId();
            uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, IntPtr.Zero) : 0u;
            uint targetThread = GetWindowThreadProcessId(target, IntPtr.Zero);


            bool attached = fgThread != 0 && fgThread != current && targetThread != 0
                && AttachThreadInput(current, fgThread, true);
            try
            {
                return SetForegroundWindow(target);
            }
            finally
            {
                if (attached) AttachThreadInput(current, fgThread, false);
            }
        }
        catch { return false; }
    }

    internal static void Log(string msg)
    {
        string line = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, msg);
        var sink = LogSink;
        if (sink != null) sink(line);
        else Debug.WriteLine(line);
    }
}

// Main window: start/stop the watcher, switch hidden mode, show the log
class MainForm : Form
{
    readonly Button _toggleBtn = new Button();
    readonly Label _status = new Label();
    readonly CheckBox _hiddenCheck = new CheckBox();
    readonly TextBox _log = new TextBox();

    // Log lines that arrived before the window handle existed
    readonly Queue<string> _pending = new Queue<string>();

    public MainForm()
    {
        Text = "Авто-Принятие для KodaCode";
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(640, 400);
        MinimumSize = new Size(520, 320);
        StartPosition = FormStartPosition.CenterScreen;

        _toggleBtn.Text = "Включить";
        _toggleBtn.Location = new Point(12, 12);
        _toggleBtn.Size = new Size(150, 34);
        _toggleBtn.Click += delegate { Toggle(); };

        _status.AutoSize = true;
        _status.Location = new Point(174, 22);
        _status.ForeColor = Color.Gray;

        _hiddenCheck.AutoSize = true;
        _hiddenCheck.Checked = AutoAccept.HiddenMode;
        _hiddenCheck.Location = new Point(12, 58);
        _hiddenCheck.Text = "Hidden mode (VS Code не активировать, фокус возвращать)";
        _hiddenCheck.CheckedChanged += delegate { OnHiddenModeChanged(); };

        _log.Location = new Point(12, 86);
        _log.Size = new Size(616, 302);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.White;
        _log.Font = new Font("Consolas", 9f);

        Controls.Add(_toggleBtn);
        Controls.Add(_status);
        Controls.Add(_hiddenCheck);
        Controls.Add(_log);

        AutoAccept.LogSink = WriteLog;
        UpdateControls();
        FormClosing += delegate { AutoAccept.Stop(); AutoAccept.LogSink = null; };

        AutoAccept.Log("Ready. Watching buttons: " + string.Join(", ", AutoAccept.Patterns));
        if (AutoAccept.DryRun) AutoAccept.Log("DRY-RUN mode: matches will be logged but not clicked.");
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        lock (_pending)
            while (_pending.Count > 0)
                AppendLine(_pending.Dequeue());
    }

    void Toggle()
    {
        if (AutoAccept.Running)
        {
            AutoAccept.Stop();
            AutoAccept.Log("Stopped.");
        }
        else
        {
            AutoAccept.Log("Started.");
            AutoAccept.Start();
        }

        UpdateControls();
    }

    void OnHiddenModeChanged()
    {
        AutoAccept.HiddenMode = _hiddenCheck.Checked;
        AutoAccept.Log(_hiddenCheck.Checked
            ? "Hidden mode ON: VS Code is not activated, focus is restored if stolen"
            : "Hidden mode OFF: VS Code is activated before each click");
    }

    void UpdateControls()
    {
        bool running = AutoAccept.Running;
        _toggleBtn.Text = running ? "Выключить" : "Включить";
        _status.Text = !running ? "Остановлено"
            : (AutoAccept.DryRun ? "Работает (пробный режим, ничего не нажимается)" : "Работает");
        _status.ForeColor = running ? Color.ForestGreen : Color.Gray;
    }

    // Called from the watcher thread, so the text goes to the UI thread
    void WriteLog(string line)
    {
        if (!IsHandleCreated)
        {
            lock (_pending) _pending.Enqueue(line);
            return;
        }

        try { BeginInvoke(new Action<string>(AppendLine), line); }
        catch { } // the window is being closed
    }

    void AppendLine(string line)
    {
        const int MaxChars = 200000; // do not let the log grow without a limit
        if (_log.TextLength > MaxChars)
            _log.Text = _log.Text.Substring(_log.TextLength - MaxChars / 2);

        _log.AppendText(line + Environment.NewLine);
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        AutoAccept.Configure(args);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
