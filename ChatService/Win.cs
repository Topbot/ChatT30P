using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Diagnostics;
using System.Collections.Generic;

public static class WindowFocusService
{
    private static void LogInfo(string message)
    {
        try { Trace.WriteLine($"[Win] {DateTime.UtcNow:O} {message}"); } catch { }
        try { Console.WriteLine($"[Win] {DateTime.UtcNow:O} {message}"); } catch { }
        try { EventLog.WriteEntry("ChatService", message, EventLogEntryType.Information); } catch { }
    }

    private static void LogError(string message)
    {
        try { Trace.WriteLine($"[Win][ERR] {DateTime.UtcNow:O} {message}"); } catch { }
        try { Console.WriteLine($"[Win][ERR] {DateTime.UtcNow:O} {message}"); } catch { }
        try { EventLog.WriteEntry("ChatService", message, EventLogEntryType.Error); } catch { }
    }

    private static bool RunUiaStep(string stepName, Func<bool> action, int timeoutMs = 4000)
    {
        LogInfo($"UIA step start: {stepName}");
        try
        {
            // UIAutomation may deadlock or run extremely slow when invoked from a background thread.
            // Execute inline on the current thread.
            var started = DateTime.UtcNow;
            bool result;
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                LogError($"UIA step ERROR: {stepName} :: {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            if (elapsed > timeoutMs)
                LogError($"UIA step SLOW ({(int)elapsed}ms > {timeoutMs}ms): {stepName}");

            LogInfo($"UIA step done: {stepName} => {result} ({(int)elapsed}ms)");
            return result;
        }
        catch (Exception ex)
        {
            LogError($"UIA step wrapper ERROR: {stepName} :: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // UIA-only: open filter panel, set Profile ID in first unnamed, enabled, focusable, not-readonly Edit
    private static bool TrySetProfileIdUiaOnly(IntPtr hWnd, string adsPowerId)
    {
        try
        {
            var root = AutomationElement.FromHandle(hWnd);
            if (root == null)
            {
                LogError("UIA-only: root is null");
                return false;
            }

            // Open filter panel by invoking Profile ID text
            LogInfo("UIA-only: searching for Profile ID text to open panel...");
            var opener = FindByPredicateNoDescendants(root,
                el => el.Current.ControlType == ControlType.Text &&
                      (el.Current.Name ?? "").StartsWith("Profile ID", StringComparison.OrdinalIgnoreCase),
                nodeBudget: 1000, timeBudgetMs: 2000);
            if (opener == null)
            {
                LogInfo("UIA-only: Profile ID text not found; assuming panel is already open, searching for edit field...");
            }
            else
            {
                LogInfo("UIA-only: Profile ID text found, invoking...");
                if (opener.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
                    ((InvokePattern)inv).Invoke();
                else
                    TryDoLegacyDefaultAction(opener);
                ThreadSleep(400);
            }

            // Find first unnamed, enabled, focusable, not-readonly Edit
            LogInfo("UIA-only: searching for unnamed edit field...");
            var input = FindByPredicateNoDescendants(root,
                el => el.Current.ControlType == ControlType.Edit
                      && string.IsNullOrEmpty(el.Current.Name)
                      && el.Current.IsEnabled
                      && (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vpp) ? string.IsNullOrEmpty(((ValuePattern)vpp).Current.Value) : true),
                nodeBudget: 2000, timeBudgetMs: 4000);
            if (input == null)
            {
                LogError("UIA-only: unnamed edit not found");
                // Log all edits for diagnostics
                LogInfo("UIA-only: listing all Edit elements for diagnostics...");
                var allEdits = FindAllEdits(root);
                foreach (var edit in allEdits)
                {
                    LogElementInfo("Edit", edit);
                }
                return false;
            }
            LogInfo("UIA-only: unnamed edit found, setting value...");
            if (input.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                ((ValuePattern)vp).SetValue(adsPowerId);
            else
            {
                LogError("UIA-only: edit has no ValuePattern");
                return false;
            }

            // Verify if the value was set correctly
            try
            {
                var after = ((ValuePattern)vp).Current.Value;
                LogInfo($"UIA-only: TargetInput ValuePattern after set => '{after}'");
                if (!string.Equals(after, adsPowerId, StringComparison.Ordinal))
                {
                    LogInfo("UIA-only: Value not set correctly, falling back to keyboard input");
                    try { input.SetFocus(); } catch { }
                    SendCtrlA(); ThreadSleep(80);
                    SendBackspace(); ThreadSleep(120);
                    SendText(adsPowerId);
                    LogInfo($"UIA-only: typed value via keyboard => '{adsPowerId}'");
                }
            }
            catch (Exception ex)
            {
                LogError($"UIA-only: failed to verify value: {ex.Message}, falling back to keyboard");
                try { input.SetFocus(); } catch { }
                SendCtrlA(); ThreadSleep(80);
                SendBackspace(); ThreadSleep(120);
                SendText(adsPowerId);
                LogInfo($"UIA-only: typed value via keyboard => '{adsPowerId}'");
            }

            // Find and click Confirm button
            LogInfo("UIA-only: searching for Confirm button...");
            var confirm = FindByPredicateNoDescendants(root,
                el => el.Current.ControlType == ControlType.Button &&
                      string.Equals(el.Current.Name, "Confirm", StringComparison.OrdinalIgnoreCase),
                nodeBudget: 600, timeBudgetMs: 2000);
            if (confirm == null)
            {
                LogError("UIA-only: Confirm button not found");
                // Log all buttons for diagnostics
                LogInfo("UIA-only: listing all Button elements for diagnostics...");
                var allButtons = FindAllButtons(root);
                foreach (var btn in allButtons)
                {
                    LogElementInfo("Button", btn);
                }
                return false;
            }
            LogInfo("UIA-only: Confirm found, invoking...");
            if (confirm.TryGetCurrentPattern(InvokePattern.Pattern, out var invC))
                ((InvokePattern)invC).Invoke();
            else if (!TryDoLegacyDefaultAction(confirm))
            {
                // Fallback: click center of Confirm button
                try
                {
                    var brObj = confirm.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty);
                    if (brObj != null)
                    {
                        var t = brObj.GetType();
                        var leftProp = t.GetProperty("Left") ?? t.GetProperty("X");
                        var topProp = t.GetProperty("Top") ?? t.GetProperty("Y");
                        var rightProp = t.GetProperty("Right");
                        var bottomProp = t.GetProperty("Bottom");
                        double left = leftProp != null ? Convert.ToDouble(leftProp.GetValue(brObj, null)) : double.NaN;
                        double top = topProp != null ? Convert.ToDouble(topProp.GetValue(brObj, null)) : double.NaN;
                        double right = rightProp != null ? Convert.ToDouble(rightProp.GetValue(brObj, null)) : left + 1;
                        double bottom = bottomProp != null ? Convert.ToDouble(bottomProp.GetValue(brObj, null)) : top + 1;
                        if (!double.IsNaN(left) && !double.IsNaN(top))
                        {
                            int cx = (int)Math.Round((left + right) / 2.0);
                            int cy = (int)Math.Round((top + bottom) / 2.0);
                            ClickScreenPoint(cx, cy);
                            ThreadSleep(160);
                        }
                        else
                        {
                            LogError("UIA-only: Confirm not invokable");
                            return false;
                        }
                    }
                    else
                    {
                        LogError("UIA-only: Confirm not invokable");
                        return false;
                    }
                }
                catch
                {
                    LogError("UIA-only: Confirm not invokable");
                    return false;
                }
            }

            LogInfo("UIA-only: success");
            return true;
        }
        catch (Exception ex)
        {
            LogError($"UIA-only: exception {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static bool TryInvokeRpaAndPlusUia(IntPtr hWnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hWnd);
            if (root == null)
                return false;

            // Wait for window content to stabilize (max 8 seconds)
            if (!WaitForCondition(hWnd, r => FindByPredicateNoDescendants(r,
                el => el.Current.ControlType == ControlType.DataItem, 300, 2500) != null, 8000, 400))
            {
                LogError("UIA: Timeout waiting for table rows to appear");
                return false;
            }

            // Find RPA button by Name 'RPA'
            var rpa = FindByPredicateNoDescendants(root,
                el => el.Current.ControlType == ControlType.Button && string.Equals(el.Current.Name, "RPA", StringComparison.OrdinalIgnoreCase),
                nodeBudget: 3000,
                timeBudgetMs: 5000);

            if (rpa == null)
            {
                LogInfo("UIA: RPA button not found");
                // Log all buttons for diagnostics
                LogInfo("UIA: listing all Button elements for diagnostics...");
                var allButtons = FindAllButtons(root);
                foreach (var btn in allButtons)
                {
                    LogElementInfo("Button", btn);
                }
                return false;
            }
            // Prefer clicking the center of the control to avoid hitting nearby elements (use reflection to avoid WindowsBase Rect type)
            try
            {
                var brObj = rpa.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty);
                if (brObj != null)
                {
                    var t = brObj.GetType();
                    var leftProp = t.GetProperty("Left") ?? t.GetProperty("X");
                    var topProp = t.GetProperty("Top") ?? t.GetProperty("Y");
                    var rightProp = t.GetProperty("Right");
                    var bottomProp = t.GetProperty("Bottom");
                    double left = leftProp != null ? Convert.ToDouble(leftProp.GetValue(brObj, null)) : double.NaN;
                    double top = topProp != null ? Convert.ToDouble(topProp.GetValue(brObj, null)) : double.NaN;
                    double right = rightProp != null ? Convert.ToDouble(rightProp.GetValue(brObj, null)) : left + 1;
                    double bottom = bottomProp != null ? Convert.ToDouble(bottomProp.GetValue(brObj, null)) : top + 1;
                    if (!double.IsNaN(left) && !double.IsNaN(top))
                    {
                        int cx = (int)Math.Round((left + right) / 2.0);
                        int cy = (int)Math.Round((top + bottom) / 2.0);
                        ClickScreenPoint(cx, cy);
                        ThreadSleep(160);
                    }
                    else
                    {
                        // fallback to Invoke/legacy
                        if (rpa.TryGetCurrentPattern(InvokePattern.Pattern, out var invRpa))
                            ((InvokePattern)invRpa).Invoke();
                        else if (!TryDoLegacyDefaultAction(rpa))
                        {
                            LogInfo("UIA: RPA button not invokable");
                            return false;
                        }
                    }
                }
                else
                {
                    if (rpa.TryGetCurrentPattern(InvokePattern.Pattern, out var invRpa))
                        ((InvokePattern)invRpa).Invoke();
                    else if (!TryDoLegacyDefaultAction(rpa))
                    {
                        LogInfo("UIA: RPA button not invokable");
                        return false;
                    }
                }
            }
            catch
            {
                if (rpa.TryGetCurrentPattern(InvokePattern.Pattern, out var invRpa))
                    ((InvokePattern)invRpa).Invoke();
                else if (!TryDoLegacyDefaultAction(rpa))
                {
                    LogInfo("UIA: RPA button not invokable");
                    return false;
                }
            }

            ThreadSleep(500);

            // After RPA pressed, the 'RPA Plus' element may appear. Try a few name variants.
            var plus = FindByPredicateNoDescendants(root,
                el => el.Current.ControlType == ControlType.Button &&
                      ((el.Current.Name ?? string.Empty).IndexOf("RPA Plus", StringComparison.OrdinalIgnoreCase) >= 0
                       || (el.Current.Name ?? string.Empty).IndexOf("RPA+", StringComparison.OrdinalIgnoreCase) >= 0),
                nodeBudget: 800,
                timeBudgetMs: 2000);

            if (plus == null)
            {
                LogInfo("UIA: RPA Plus button not found after RPA press");
                // Log all buttons for diagnostics
                LogInfo("UIA: listing all Button elements after RPA press for diagnostics...");
                var allButtons = FindAllButtons(root);
                foreach (var btn in allButtons)
                {
                    LogElementInfo("ButtonAfterRPA", btn);
                }
                return false;
            }
            try
            {
                var brObj = plus.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty);
                if (brObj != null)
                {
                    var t = brObj.GetType();
                    var leftProp = t.GetProperty("Left") ?? t.GetProperty("X");
                    var topProp = t.GetProperty("Top") ?? t.GetProperty("Y");
                    var rightProp = t.GetProperty("Right");
                    var bottomProp = t.GetProperty("Bottom");
                    double left = leftProp != null ? Convert.ToDouble(leftProp.GetValue(brObj, null)) : double.NaN;
                    double top = topProp != null ? Convert.ToDouble(topProp.GetValue(brObj, null)) : double.NaN;
                    double right = rightProp != null ? Convert.ToDouble(rightProp.GetValue(brObj, null)) : left + 1;
                    double bottom = bottomProp != null ? Convert.ToDouble(bottomProp.GetValue(brObj, null)) : top + 1;
                    if (!double.IsNaN(left) && !double.IsNaN(top))
                    {
                        int cx = (int)Math.Round((left + right) / 2.0);
                        int cy = (int)Math.Round((top + bottom) / 2.0);
                        ClickScreenPoint(cx, cy);
                        ThreadSleep(160);
                        return true;
                    }
                }

                if (plus.TryGetCurrentPattern(InvokePattern.Pattern, out var invPlus))
                {
                    ((InvokePattern)invPlus).Invoke();
                    return true;
                }

                if (TryDoLegacyDefaultAction(plus))
                    return true;
            }
            catch
            {
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
    private static void LogElementInfo(string prefix, AutomationElement el)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(prefix);
            sb.Append($": CtrlType={el.Current.ControlType.ProgrammaticName}");
            sb.Append($", Name='{el.Current.Name}'");
            sb.Append($", AutomationId='{el.Current.AutomationId}'");
            sb.Append($", IsKeyboardFocusable={el.Current.IsKeyboardFocusable}");
            sb.Append($", IsEnabled={el.Current.IsEnabled}");
            try
            {
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var v))
                    sb.Append($", Value='" + ((ValuePattern)v).Current.Value + "'");
            }
            catch { }

            LogInfo(sb.ToString());
        }
        catch { }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;
    private const byte VK_MENU = 0x12; // ALT
    private const int KEYEVENTF_KEYUP = 0x0002;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    /// <summary>
    /// Фокусирует первое окно, заголовок которого начинается с "AdsPower Browser"
    /// </summary>
    /// <returns>true если окно найдено и сфокусировано</returns>
    public static bool FocusAdsPower()
    {
        bool focused = false;

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;

            int length = GetWindowTextLength(hWnd);
            if (length == 0)
                return true;

            var titleBuilder = new StringBuilder(length + 1);
            GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);

            string title = titleBuilder.ToString();

            if (title.StartsWith("AdsPower Browser", StringComparison.OrdinalIgnoreCase))
            {
                // ALT-хак против блокировки SetForegroundWindow
                keybd_event(VK_MENU, 0, 0, 0);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);

                ShowWindow(hWnd, SW_RESTORE);
                SetForegroundWindow(hWnd);
                ShowWindow(hWnd, SW_MAXIMIZE);

                focused = true;
                return false; // остановить EnumWindows
            }

            return true;
        }, IntPtr.Zero);

        return focused;
    }

    public static void SelectProfileIdAndFill(string adsPowerId, string scriptName = null)
    {
        if (string.IsNullOrWhiteSpace(adsPowerId))
            return;

        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
            return;

        ShowWindow(hWnd, SW_RESTORE);
        SetForegroundWindow(hWnd);
        Thread.Sleep(150);

        if (!GetClientRect(hWnd, out var rc))
            return;

        int width = rc.Right - rc.Left;
        int height = rc.Bottom - rc.Top;
        if (width <= 0 || height <= 0)
            return;

        // Try to invoke Create task button via WinAppDriver
        var ok = TryInvokeCreateTaskWinAppDriver(hWnd);
        if (!ok)
        {
            LogError("Create task button not found/invoked via WinAppDriver");
        }
    }

    private static bool TryFillProfileIdAndConfirmUiaNoDescendants(IntPtr hWnd, string adsPowerId)
    {
        try
        {
            var root = AutomationElement.FromHandle(hWnd);
            if (root == null)
                return false;

            AutomationElement profileIdText = null;

            // Ensure filter panel is open by invoking Profile ID text opener first.
            LogInfo("UIA-only: invoking Profile ID opener to ensure filter panel is open...");
            if (!TryOpenFilterPanelUiaNoDescendants(root))
            {
                LogInfo("UIA-only: opener invocation failed or panel may already be open; continuing");
            }
            ThreadSleep(800);

            // Now find Confirm within the window (anchored search after opening the panel).
            var confirm = FindByPredicateNoDescendants(root,
                el => el.Current.ControlType == ControlType.Button &&
                      string.Equals(el.Current.Name, "Confirm", StringComparison.OrdinalIgnoreCase),
                nodeBudget: 800,
                timeBudgetMs: 4000);

            if (confirm == null)
            {
                LogError("UIA-only: Confirm button not found (panel may be closed)");
                return false;
            }

            LogInfo("UIA-only: Confirm found");

            // If the focused element is already the edit we need, use it directly to set the value.
            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused != null && focused.Current.ControlType == ControlType.Edit)
                {
                    LogInfo("UIA-only: focused element is Edit; using focused element for input");
                    bool filledViaFocus = false;
                    try
                    {
                        if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out var vpf))
                        {
                            ((ValuePattern)vpf).SetValue(adsPowerId);
                            filledViaFocus = true;
                            LogInfo($"UIA-only: set value via ValuePattern on focused element => '{adsPowerId}'");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError($"UIA-only: ValuePattern.SetValue on focused element failed: {ex.Message}");
                    }

                    if (!filledViaFocus)
                    {
                        try { focused.SetFocus(); } catch { }
                        SendCtrlA(); ThreadSleep(80);
                        SendBackspace(); ThreadSleep(120);
                        SendText(adsPowerId);
                        LogInfo($"UIA-only: typed value into focused element => '{adsPowerId}'");
                    }

                    // Now invoke Confirm using existing logic below.
                    if (confirm.TryGetCurrentPattern(InvokePattern.Pattern, out var invf))
                    {
                        ((InvokePattern)invf).Invoke();
                        LogInfo("UIA-only: Confirm invoked after focused input");
                        return true;
                    }

                    if (TryDoLegacyDefaultAction(confirm))
                    {
                        LogInfo("UIA-only: Confirm DoDefaultAction executed after focused input");
                        return true;
                    }

                    LogError("UIA-only: Confirm not invokable after focused input");
                    return false;
                }
            }
            catch
            {
            }

            var container = FindAncestor(confirm,
                el => el.Current.ControlType == ControlType.Pane || el.Current.ControlType == ControlType.Window || el.Current.ControlType == ControlType.Group,
                maxHops: 10);

            if (container == null)
                container = root;

            LogInfo("UIA-only: searching for unnamed edit (edit:'') near Confirm/container...");

            AutomationElement input = null;

            // First try: find 'Select' edit and take next sibling edits — common layout where next edit is the value field.
            LogInfo("UIA-only: locating edit 'Select' to anchor search...");
            var selectEdit = FindByPredicateNoDescendants(root,
                el => el.Current.ControlType == ControlType.Edit &&
                      string.Equals(el.Current.Name, "Select", StringComparison.OrdinalIgnoreCase),
                nodeBudget: 800,
                timeBudgetMs: 1500);

            if (selectEdit != null)
            {
                LogElementInfo("SelectEdit", selectEdit);
                var walker = TreeWalker.ControlViewWalker;
                var next = walker.GetNextSibling(selectEdit);
                int hops = 0;
                while (next != null && hops < 6)
                {
                    LogElementInfo($"SelectSibling[{hops}]", next);
                    try
                    {
                        if (next.Current.ControlType == ControlType.Edit
                            && string.IsNullOrEmpty(next.Current.Name)
                            && next.Current.IsKeyboardFocusable
                            && next.Current.IsEnabled)
                        {
                            bool isReadOnly = false;
                            try { if (next.TryGetCurrentPattern(ValuePattern.Pattern, out var vpat)) isReadOnly = ((ValuePattern)vpat).Current.IsReadOnly; } catch { }
                            if (!isReadOnly)
                            {
                                input = next;
                                LogInfo("UIA-only: found unnamed edit as sibling after 'Select'");
                                break;
                            }
                        }
                    }
                    catch { }
                    next = walker.GetNextSibling(next);
                    hops++;
                }
            }

            // Second try: search for unnamed edit inside the container
            if (input == null)
            {
                LogInfo("UIA-only: searching for unnamed edit inside Confirm container...");
                input = FindByPredicateNoDescendants(container,
                    el => el.Current.ControlType == ControlType.Edit
                          && string.IsNullOrEmpty(el.Current.Name)
                          && el.Current.IsKeyboardFocusable
                          && el.Current.IsEnabled,
                    nodeBudget: 800,
                    timeBudgetMs: 2000);
            }

            // Third try: search from root as last resort
            if (input == null)
            {
                LogInfo("UIA-only: searching for unnamed edit from root as last resort...");
                input = FindByPredicateNoDescendants(root,
                    el => el.Current.ControlType == ControlType.Edit
                          && string.IsNullOrEmpty(el.Current.Name)
                          && el.Current.IsKeyboardFocusable
                          && el.Current.IsEnabled,
                    nodeBudget: 1200,
                    timeBudgetMs: 3000);
            }

            if (input == null)
            {
                LogError("UIA-only: input field not found (unnamed edit)");
                return false;
            }

            LogElementInfo("TargetInput", input);

            // Try set via ValuePattern; fallback to keyboard typing.
            if (input.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            {
                try
                {
                    ((ValuePattern)vp).SetValue(adsPowerId);
                }
                catch (Exception ex)
                {
                    LogError($"UIA-only: ValuePattern.SetValue failed: {ex.Message}");
                    try { input.SetFocus(); } catch { }
                    SendCtrlA(); ThreadSleep(80);
                    SendBackspace(); ThreadSleep(120);
                    SendText(adsPowerId);
                }
            }
            else
            {
                try { input.SetFocus(); } catch { }
                SendCtrlA(); ThreadSleep(80);
                SendBackspace(); ThreadSleep(120);
                SendText(adsPowerId);
            }

            // Verify best-effort that the value was applied.
            try
            {
                if (vp != null)
                {
                    var after = ((ValuePattern)vp).Current.Value;
                    LogInfo($"UIA-only: TargetInput ValuePattern after set => '{after}'");
                }
            }
            catch { }

            if (confirm.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
            {
                ((InvokePattern)inv).Invoke();
                return true;
            }

            if (TryDoLegacyDefaultAction(confirm))
            {
                LogInfo("UIA-only: Confirm DoDefaultAction executed");
                return true;
            }

            LogError("UIA-only: Confirm has no InvokePattern and no legacy default action");
            return false;
        }
        catch (Exception ex)
        {
            LogError($"UIA-only failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryOpenFilterPanelUiaNoDescendants(AutomationElement root)
    {
        try
        {
            // This AdsPower build exposes a clickable label as:
            // ControlType=Text, Name like "Profile ID is 234234", DefaultAction="click ancestor".
            // We locate that text (bounded TreeWalker search) and execute default action.

            var profileIdText = FindByPredicateNoDescendants(root,
                el =>
                {
                    if (el.Current.ControlType != ControlType.Text)
                        return false;
                    var name = el.Current.Name ?? string.Empty;
                    return name.StartsWith("Profile ID", StringComparison.OrdinalIgnoreCase);
                },
                nodeBudget: 1200,
                timeBudgetMs: 3000);

            if (profileIdText == null)
            {
                LogError("UIA-only: Profile ID text not found (cannot open panel)");
                return false;
            }

            // Try InvokePattern first
            if (profileIdText.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
            {
                ((InvokePattern)inv).Invoke();
                LogInfo("UIA-only: Profile ID text invoked");
                return true;
            }

            // Fallback: LegacyIAccessible default action (handles 'click ancestor')
            if (TryDoLegacyDefaultAction(profileIdText))
            {
                LogInfo("UIA-only: Profile ID text DoDefaultAction executed");
                return true;
            }

            // As a last resort, try parent default action (ancestor click)
            var ancestor = FindAncestor(profileIdText, _ => true, maxHops: 4);
            if (ancestor != null && TryDoLegacyDefaultAction(ancestor))
            {
                LogInfo("UIA-only: ancestor DoDefaultAction executed");
                return true;
            }

            LogError("UIA-only: could not invoke Profile ID opener");
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDoLegacyDefaultAction(AutomationElement element)
    {
        try
        {
            // Avoid compile-time dependency on LegacyIAccessiblePattern type.
            var pat = AutomationPattern.LookupById(10018); // LegacyIAccessiblePatternIdentifiers.Pattern.Id
            if (pat == null)
                return false;

            if (!element.TryGetCurrentPattern(pat, out var obj) || obj == null)
                return false;

            var mi = obj.GetType().GetMethod("DoDefaultAction");
            if (mi == null)
                return false;

            mi.Invoke(obj, null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetLegacyValue(AutomationElement element, out string value)
    {
        value = null;
        try
        {
            var pat = AutomationPattern.LookupById(10018); // LegacyIAccessiblePatternIdentifiers.Pattern.Id
            if (pat == null)
                return false;

            if (!element.TryGetCurrentPattern(pat, out var obj) || obj == null)
                return false;

            var prop = obj.GetType().GetProperty("Current");
            if (prop == null)
                return false;

            var current = prop.GetValue(obj, null);
            if (current == null)
                return false;

            var valProp = current.GetType().GetProperty("Value");
            if (valProp == null)
                return false;

            value = valProp.GetValue(current, null) as string;
            return value != null;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static bool IsFocusedEditAutomationId(string automationId)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null)
                return false;
            if (focused.Current.ControlType != ControlType.Edit)
                return false;
            return string.Equals(focused.Current.AutomationId, automationId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool HasOpenSunBrowserWindow()
    {
        bool found = false;

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;

            int length = GetWindowTextLength(hWnd);
            if (length == 0)
                return true;

            var titleBuilder = new StringBuilder(length + 1);
            GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
            var title = titleBuilder.ToString();

            if (title.EndsWith(" - SunBrowser", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    public static int CloseSunBrowserWindows()
    {
        int closed = 0;

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;

            int length = GetWindowTextLength(hWnd);
            if (length == 0)
                return true;

            var titleBuilder = new StringBuilder(length + 1);
            GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
            var title = titleBuilder.ToString();

            if (title.EndsWith(" - SunBrowser", StringComparison.OrdinalIgnoreCase))
            {
                // Request close
                PostMessage(hWnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                closed++;
            }

            return true;
        }, IntPtr.Zero);

        return closed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint dwFlags;
        public uint dwTime;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint dwTime;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)]
        public uint type;
        [FieldOffset(4)]
        public MOUSEINPUT mi;
        [FieldOffset(4)]
        public KEYBDINPUT ki;
        [FieldOffset(4)]
        public HARDWAREINPUT hi;
    }

    private static void ThreadSleep(int ms)
    {
        try { System.Threading.Thread.Sleep(ms); } catch { }
    }

    private static void ClickClientPoint(IntPtr hWnd, int xClient, int yClient, int width, int height)
    {
        xClient = Math.Max(0, Math.Min(width - 1, xClient));
        yClient = Math.Max(0, Math.Min(height - 1, yClient));

        var pt = new POINT { X = xClient, Y = yClient };
        if (!ClientToScreen(hWnd, ref pt))
            return;

        ClickScreenPoint(pt.X, pt.Y);
    }

    private static void ClickScreenPoint(int x, int y)
    {
        // В режиме отладки плавно двигаем курсор к точке клика, чтобы было видно куда попадаем.
        if (Environment.UserInteractive)
            SmoothMoveMouseTo(x, y, steps: 18, stepDelayMs: 12);

        // SendInput ожидает абсолютные координаты 0..65535
        int normalizedX = (int)Math.Round(x * 65535.0 / (GetSystemMetrics(0) - 1));
        int normalizedY = (int)Math.Round(y * 65535.0 / (GetSystemMetrics(1) - 1));

        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = normalizedX,
                    dy = normalizedY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
                }
            },
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = normalizedX,
                    dy = normalizedY,
                    dwFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE
                }
            },
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = normalizedX,
                    dy = normalizedY,
                    dwFlags = MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE
                }
            }
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    private static void SmoothMoveMouseTo(int targetX, int targetY, int steps, int stepDelayMs)
    {
        try
        {
            if (steps < 1)
                steps = 1;

            // Без System.Windows.Forms: двигаем мышь через SendInput абсолютными координатами.
            // Стартовую позицию не читаем — просто делаем серию Move к конечной точке.
            for (int i = 1; i <= steps; i++)
            {
                int x = targetX;
                int y = targetY;
                MoveMouseScreenPoint(x, y);
                ThreadSleep(stepDelayMs);
            }
        }
        catch
        {
        }
    }

    private static void MoveMouseScreenPoint(int x, int y)
    {
        int normalizedX = (int)Math.Round(x * 65535.0 / (GetSystemMetrics(0) - 1));
        int normalizedY = (int)Math.Round(y * 65535.0 / (GetSystemMetrics(1) - 1));

        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = normalizedX,
                    dy = normalizedY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
                }
            }
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    private static void SendText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        // Вставка через Ctrl+V была бы надежнее, но без буфера делаем простую отправку unicode.
        foreach (char ch in text)
            SendUnicodeChar(ch);
    }

    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP_EX = 0x0002;

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_A = 0x41;
    private const ushort VK_BACK = 0x08;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_TAB = 0x09;

    private static void SendCtrlA()
    {
        SendKeyDown(VK_CONTROL);
        SendKeyDown(VK_A);
        SendKeyUp(VK_A);
        SendKeyUp(VK_CONTROL);
    }

    private static void SendBackspace()
    {
        SendKeyDown(VK_BACK);
        SendKeyUp(VK_BACK);
    }

    private static void SendEnter()
    {
        SendKeyDown(VK_RETURN);
        SendKeyUp(VK_RETURN);
    }

    private static void SendTab()
    {
        SendKeyDown(VK_TAB);
        SendKeyUp(VK_TAB);
    }

    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private static void SendKeyPressScan(ushort scanCode)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scanCode,
                    dwFlags = KEYEVENTF_SCANCODE
                }
            },
            new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scanCode,
                    dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP_EX
                }
            }
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    private static void SendKeyDown(ushort vk)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = 0
                }
            }
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    private static void SendKeyUp(ushort vk)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = KEYEVENTF_KEYUP_EX
                }
            }
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    private static void SendUnicodeChar(char ch)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT
                {
                    wScan = ch,
                    dwFlags = KEYEVENTF_UNICODE
                }
            },
            new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT
                {
                    wScan = ch,
                    dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP_EX
                }
            }
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static void ClickRpaAndNext(IntPtr hWnd, int width, int height)
    {
        // Координаты кнопки RPA (подобрать при необходимости)
        const int rpaX = 300;
        const int rpaY = 180;
        const int rpaButtonHeight = 28;

        ClickClientPoint(hWnd, rpaX, rpaY, width, height);
        ThreadSleep(350);

        int nextY = rpaY + (int)Math.Round(rpaButtonHeight * 1.5);
        ClickClientPoint(hWnd, rpaX, nextY, width, height);
        ThreadSleep(300);
    }

    private static void FillRpaScriptAndOk(IntPtr hWnd, int width, int height, string scriptName)
    {
        // Поле "write here" в диалоге RPA Plus
        // Координаты требуют подстройки под ваш UI.
        const int writeHereX = 820;
        const int writeHereY = 410;

        // Кнопка OK
        const int okX = 600;
        const int okY = 650;

        ClickClientPoint(hWnd, writeHereX, writeHereY, width, height);
        ThreadSleep(350);

        SendCtrlA();
        ThreadSleep(120);
        SendBackspace();
        ThreadSleep(180);
        SendText(scriptName);
        ThreadSleep(2000);

        // Клик на одну строку ниже поля ввода (обычно выбирает пункт из выпадающего списка)
        ClickClientPoint(hWnd, writeHereX, writeHereY + 45, width, height);
        ThreadSleep(300);

        ClickClientPoint(hWnd, okX, okY, width, height);
        ThreadSleep(400);
    }

    public static void ClickRpaDialogCancel()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
            return;

        if (!GetClientRect(hWnd, out var rc))
            return;

        int width = rc.Right - rc.Left;
        int height = rc.Bottom - rc.Top;
        if (width <= 0 || height <= 0)
            return;

        // Кнопка Cancel рядом с OK
        const int okX = 600;
        const int okY = 650;
        ClickClientPoint(hWnd, okX + 80, okY, width, height);
        ThreadSleep(400);
    }

    private static bool TryClickSearchSettingsIcon(IntPtr hWnd)
    {
        try
        {
            if (hWnd == IntPtr.Zero)
                return false;

            // Убедимся, что окно реально на переднем плане
            if (GetForegroundWindow() != hWnd)
                SetForegroundWindow(hWnd);

            if (!GetClientRect(hWnd, out var rc))
                return false;

            int width = rc.Right - rc.Left;
            int height = rc.Bottom - rc.Top;
            if (width <= 0 || height <= 0)
                return false;

            // Примерные смещения для клика: справа от поля поиска, рядом с иконкой "ползунков".
            // X: от правого края клиентской области (после maximize)
            // Y: высота строки поиска (верхняя панель)
            int xClient = Math.Max(0, width - 580);
            // Was hitting the updates/settings button above; move one row down to the search settings icon.
            int yClient = Math.Max(0, Math.Min(height - 1, 160));

            var pt = new POINT { X = xClient, Y = yClient };
            if (!ClientToScreen(hWnd, ref pt))
                return false;

            ClickScreenPoint(pt.X, pt.Y);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AutomationElement FindByPredicateNoDescendants(AutomationElement root, Func<AutomationElement, bool> predicate, int nodeBudget, int timeBudgetMs)
    {
        var walker = TreeWalker.ControlViewWalker;
        var current = walker.GetFirstChild(root);
        int visited = 0;
        var started = DateTime.UtcNow;

        while (current != null && visited < nodeBudget)
        {
            if ((DateTime.UtcNow - started).TotalMilliseconds > timeBudgetMs)
                return null;

            visited++;
            try
            {
                if (predicate(current))
                    return current;
            }
            catch
            {
            }

            var child = walker.GetFirstChild(current);
            if (child != null)
            {
                current = child;
                continue;
            }

            while (current != null)
            {
                var next = walker.GetNextSibling(current);
                if (next != null)
                {
                    current = next;
                    break;
                }
                current = walker.GetParent(current);
                if (current == null || current.Equals(root))
                    return null;
            }
        }

        return null;
    }

    private static AutomationElement FindAncestor(AutomationElement start, Func<AutomationElement, bool> predicate, int maxHops)
    {
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var current = start;
            for (int i = 0; i < maxHops && current != null; i++)
            {
                current = walker.GetParent(current);
                if (current == null)
                    return null;
                try
                {
                    if (predicate(current))
                        return current;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static List<AutomationElement> FindAllButtons(AutomationElement root)
    {
        var buttons = new List<AutomationElement>();
        var walker = TreeWalker.ControlViewWalker;
        var current = walker.GetFirstChild(root);
        int visited = 0;
        var started = DateTime.UtcNow;

        while (current != null && visited < 1000 && (DateTime.UtcNow - started).TotalMilliseconds < 5000)
        {
            visited++;
            try
            {
                if (current.Current.ControlType == ControlType.Button)
                    buttons.Add(current);
            }
            catch { }

            var child = walker.GetFirstChild(current);
            if (child != null)
            {
                current = child;
                continue;
            }

            while (current != null)
            {
                var next = walker.GetNextSibling(current);
                if (next != null)
                {
                    current = next;
                    break;
                }
                current = walker.GetParent(current);
                if (current == null || current.Equals(root))
                    return buttons;
            }
        }

        return buttons;
    }

    private static List<AutomationElement> FindAllEdits(AutomationElement root)
    {
        var edits = new List<AutomationElement>();
        var walker = TreeWalker.ControlViewWalker;
        var current = walker.GetFirstChild(root);
        int visited = 0;
        var started = DateTime.UtcNow;

        while (current != null && visited < 1000 && (DateTime.UtcNow - started).TotalMilliseconds < 5000)
        {
            visited++;
            try
            {
                if (current.Current.ControlType == ControlType.Edit)
                    edits.Add(current);
            }
            catch { }

            var child = walker.GetFirstChild(current);
            if (child != null)
            {
                current = child;
                continue;
            }

            while (current != null)
            {
                var next = walker.GetNextSibling(current);
                if (next != null)
                {
                    current = next;
                    break;
                }
                current = walker.GetParent(current);
                if (current == null || current.Equals(root))
                    return edits;
            }
        }

        return edits;
    }

    private static bool WaitForCondition(IntPtr hWnd, Func<AutomationElement, bool> condition,
        int timeoutMs = 8000, int pollIntervalMs = 400)
    {
        var root = AutomationElement.FromHandle(hWnd);
        if (root == null) return false;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition(root)) return true;
            Thread.Sleep(pollIntervalMs);
        }
        return false;
    }

    private static bool TryInvokeCreateTaskUia(IntPtr hWnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hWnd);
            if (root == null)
                return false;

            // Find Create task button by Name 'Create task'
            var createTask = FindByPredicateNoDescendants(root,
                el => el.Current.ControlType == ControlType.Button &&
                      string.Equals(el.Current.Name, "Create task", StringComparison.OrdinalIgnoreCase),
                nodeBudget: 2000,
                timeBudgetMs: 4000);

            if (createTask == null)
            {
                LogInfo("UIA: Create task button not found");
                return false;
            }

            // Prefer clicking the center of the control
            try
            {
                var brObj = createTask.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty);
                if (brObj != null)
                {
                    var t = brObj.GetType();
                    var leftProp = t.GetProperty("Left") ?? t.GetProperty("X");
                    var topProp = t.GetProperty("Top") ?? t.GetProperty("Y");
                    var rightProp = t.GetProperty("Right");
                    var bottomProp = t.GetProperty("Bottom");
                    double left = leftProp != null ? Convert.ToDouble(leftProp.GetValue(brObj, null)) : double.NaN;
                    double top = topProp != null ? Convert.ToDouble(topProp.GetValue(brObj, null)) : double.NaN;
                    double right = rightProp != null ? Convert.ToDouble(rightProp.GetValue(brObj, null)) : left + 1;
                    double bottom = bottomProp != null ? Convert.ToDouble(bottomProp.GetValue(brObj, null)) : top + 1;
                    if (!double.IsNaN(left) && !double.IsNaN(top))
                    {
                        int cx = (int)Math.Round((left + right) / 2.0);
                        int cy = (int)Math.Round((top + bottom) / 2.0);
                        LogInfo($"UIA: Clicking Create task at screen {cx},{cy}");
                        // Try PostMessage click instead of SendInput
                        if (TryPostMessageClick(hWnd, cx, cy))
                        {
                            LogInfo("UIA: Create task button clicked via PostMessage");
                            return true;
                        }
                        else
                        {
                            ClickScreenPoint(cx, cy);
                            ThreadSleep(160);
                            LogInfo("UIA: Create task button clicked via center (SendInput fallback)");
                            return true;
                        }
                    }
                }

                // Fallback to Invoke/Legacy
                if (createTask.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
                {
                    ((InvokePattern)inv).Invoke();
                    LogInfo("UIA: Create task button invoked");
                    return true;
                }

                if (TryDoLegacyDefaultAction(createTask))
                {
                    LogInfo("UIA: Create task button DoDefaultAction executed");
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryPostMessageClick(IntPtr hWnd, int screenX, int screenY)
    {
        try
        {
            if (!GetWindowRect(hWnd, out var wr))
                return false;

            int clientX = screenX - wr.Left;
            int clientY = screenY - wr.Top;

            if (clientX < 0 || clientY < 0)
                return false;

            // Post WM_LBUTTONDOWN and WM_LBUTTONUP
            PostMessage(hWnd, 0x0201, IntPtr.Zero, (IntPtr)MAKELPARAM(clientX, clientY)); // WM_LBUTTONDOWN
            ThreadSleep(50);
            PostMessage(hWnd, 0x0202, IntPtr.Zero, (IntPtr)MAKELPARAM(clientX, clientY)); // WM_LBUTTONUP
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int MAKELPARAM(int x, int y)
    {
        return (y << 16) | (x & 0xFFFF);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private static bool TryInvokeCreateTaskWinAppDriver(IntPtr hWnd)
    {
        try
        {
            // Connect to WinAppDriver running on localhost:4723
            var options = new WindowsOptions();
            options.AddAdditionalCapability("app", "Root"); // Desktop session for already running apps

            var driver = new WindowsDriver<WindowsElement>(new Uri("http://127.0.0.1:4723"), options);

            // Find the Create task button by Name
            var button = driver.FindElementByName("Create task");
            if (button != null)
            {
                button.Click();
                LogInfo("WinAppDriver: Create task button clicked");
                driver.Quit();
                return true;
            }
            else
            {
                LogError("WinAppDriver: Create task button not found");
                driver.Quit();
                return false;
            }
        }
        catch (Exception ex)
        {
            LogError($"WinAppDriver: Exception {ex.Message}");
            return false;
        }
    }
}
