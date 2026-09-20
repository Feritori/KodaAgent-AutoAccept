// AutoAccept.cs
// Watches the VS Code window and automatically clicks confirmation buttons
// (like "OK" / "Allow" / "Yes") that appear in the agent chat.
//
// How it works: it uses Windows UI Automation to find buttons in the VS Code
// window and "presses" them via the Invoke pattern (or a background
// PostMessage click as a fallback). The real mouse cursor is never moved
// and the window does not need to be focused.
//
// Hidden mode (default): the VS Code window is NEVER brought to the
// foreground by this tool. No SetForegroundWindow / SetFocus / BringWindow-
// ToTop calls are made. If clicking still makes VS Code steal the focus,
// the previously focused window is given the focus back right away
// (use --no-restore-focus to disable that).
//
// Build:  build.cmd
// Run:    AutoAccept.exe [button names...] [--dry-run] [--no-restore-focus]
//         --dry-run           = only log matches, do not click (for testing)
//         --no-restore-focus  = do not return the focus to the previous window
//
// NOTE: VS Code (Electron) exposes its UI to UI Automation only when
// accessibility mode is on. If nothing is detected, start VS Code with:
//     code --force-renderer-accessibility

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

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
    static bool _restoreFocus = true;   // give the focus back if VS Code steals it

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

    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        _dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        if (args.Contains("--no-restore-focus", StringComparer.OrdinalIgnoreCase))
            _restoreFocus = false;
        _patterns = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (_patterns.Length == 0) _patterns = DefaultButtons;

        // Whole-word matching (case-insensitive) to avoid false positives,
        // e.g. "Да" must not match inside "Повторить редактирование".
        _patternRegexes = _patterns.Select(p => new Regex(
            @"(^|[^\p{L}\p{Nd}])" + Regex.Escape(p) + @"($|[^\p{L}\p{Nd}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();

        Log("AutoAccept started. Watching buttons: " + string.Join(", ", _patterns));
        if (_dryRun) Log("DRY-RUN mode: matches will be logged but not clicked.");
        Log("Hidden mode: VS Code is not activated" + (_restoreFocus ? ", focus is restored if stolen" : "") + ".");

        while (true)
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
            Log("VS Code window not found, waiting...");
            Thread.Sleep(2000);
            return;
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
                // Click in the background: remember what window had the focus
                // now, so we can give it back if VS Code grabs it while clicking.
                IntPtr focusBefore = GetForegroundWindow();

                bool pressed = TryPress(btn);
                if (!pressed) PostClick(hwnd, btn);

                _lastClickedId = id;
                _lastClickedAt = DateTime.UtcNow;
                Log((pressed ? "Clicked: " : "Sent click to: ") + "\"" + name + "\"");

                RestoreFocus(hwnd, focusBefore);
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
    // window that had it before. The tool never brings VS Code to the front.
    static void RestoreFocus(IntPtr vscodeHwnd, IntPtr focusBefore)
    {
        if (!_restoreFocus || focusBefore == IntPtr.Zero || focusBefore == vscodeHwnd) return;

        // Electron may take the focus slightly after the click, so re-check a few times.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetForegroundWindow() == vscodeHwnd)
            {
                if (ReturnFocusTo(focusBefore)) Log("Focus returned to the previous window");
                return;
            }
            Thread.Sleep(120);
        }
    }

    // SetForegroundWindow is blocked by Windows for background processes,
    // so the input queues of both threads are attached first.
    static bool ReturnFocusTo(IntPtr target)
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

    static void Log(string msg)
    {
        Console.WriteLine("[{0:HH:mm:ss}] {1}", DateTime.Now, msg);
    }
}
