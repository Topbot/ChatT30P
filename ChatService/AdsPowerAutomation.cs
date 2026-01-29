using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

// ReSharper disable PossibleNullReferenceException

namespace ChatService
{
    internal static class AdsPowerAutomation
    {
        private const int DefaultTimeoutMs = 8000;

        public static AutomationElement FindAdsPowerMainWindow()
        {
            var root = AutomationElement.RootElement;
            if (root == null) return null;

            var wins = root.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane));

            foreach (AutomationElement w in wins)
            {
                var name = (string)w.GetCurrentPropertyValue(AutomationElement.NameProperty) ?? string.Empty;
                if (name.StartsWith("AdsPower Browser", StringComparison.OrdinalIgnoreCase))
                    return w;
            }

            return null;
        }

        public static bool FocusMainWindow()
        {
            var win = FindAdsPowerMainWindow();
            if (win == null)
                return false;

            try
            {
                win.SetFocus();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryClickRpaButton()
        {
            var win = FindAdsPowerMainWindow();
            if (win == null)
                return false;

            // Common case: left navigation item "RPA" or toolbar button "RPA"
            var btn = FindWithTimeout(win,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "RPA")),
                timeoutMs: 2500);

            if (btn == null)
            {
                // Try menu item
                btn = FindWithTimeout(win,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                        new PropertyCondition(AutomationElement.NameProperty, "RPA")),
                    timeoutMs: 2500);
            }

            return TryInvoke(btn);
        }

        public static bool TryFillScriptNameAndOk(string scriptName)
        {
            if (string.IsNullOrWhiteSpace(scriptName))
                return false;

            var win = FindAdsPowerMainWindow();
            if (win == null)
                return false;

            // Find the RPA Plus dialog by title
            var dlg = FindWithTimeout(win,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane),
                    new PropertyCondition(AutomationElement.NameProperty, "RPA Plus")),
                timeoutMs: 6000);

            if (dlg == null)
                return false;

            // Find input with placeholder/name "write hwer" or "write here"
            var edit = FindWithTimeout(dlg,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                timeoutMs: 4000);

            if (edit == null)
                return false;

            if (!TrySetValue(edit, scriptName))
                return false;

            Thread.Sleep(2000);

            // If the control exposes Selection/ExpandCollapse patterns in your build, we can add it here.

            // OK button
            var okBtn = FindWithTimeout(dlg,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "OK")),
                timeoutMs: 4000);

            return TryInvoke(okBtn);
        }

        private static AutomationElement FindWithTimeout(AutomationElement root, Condition condition, int timeoutMs = DefaultTimeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var el = root.FindFirst(TreeScope.Descendants, condition);
                if (el != null)
                    return el;
                Thread.Sleep(150);
            }
            return null;
        }

        private static bool TryInvoke(AutomationElement el)
        {
            if (el == null) return false;
            try
            {
                if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var p))
                {
                    ((InvokePattern)p).Invoke();
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool TrySetValue(AutomationElement el, string value)
        {
            try
            {
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var p))
                {
                    ((ValuePattern)p).SetValue(value);
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }
    }
}
