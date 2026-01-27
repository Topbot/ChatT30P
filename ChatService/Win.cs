using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;

public static class WindowFocusService
{
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

    public static void SelectProfileIdAndFill(string adsPowerId)
    {
        if (string.IsNullOrWhiteSpace(adsPowerId))
            return;

        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
            return;

        SetForegroundWindow(hWnd);
        Thread.Sleep(150);

        if (!GetClientRect(hWnd, out var rc))
            return;

        int width = rc.Right - rc.Left;
        int height = rc.Bottom - rc.Top;
        if (width <= 0 || height <= 0)
            return;

        // Открываем панель фильтра (иконка "настройки" в строке поиска)
        // Важно: один клик. Повторный клик может закрыть панель.
        TryClickSearchSettingsIcon(hWnd);
        Thread.Sleep(700);

        // Фокусируем поле значения (там где 111111...) кликом мыши
        ClickClientPoint(hWnd, xClient: 930, yClient: 170, width, height);
        ThreadSleep(350);

        // Переписываем значение и применяем (Enter)
        SendCtrlA();
        ThreadSleep(120);
        SendBackspace();
        ThreadSleep(180);
        SendText(adsPowerId);
        ThreadSleep(300);
        SendEnter();
        ThreadSleep(300);
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
            int yClient = Math.Max(0, Math.Min(height - 1, 118));

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

    private static void ClickClientPoint(IntPtr hWnd, int xClient, int yClient, int width, int height)
    {
        xClient = Math.Max(0, Math.Min(width - 1, xClient));
        yClient = Math.Max(0, Math.Min(height - 1, yClient));

        var pt = new POINT { X = xClient, Y = yClient };
        if (!ClientToScreen(hWnd, ref pt))
            return;

        ClickScreenPoint(pt.X, pt.Y);
    }

    private static void ThreadSleep(int ms)
    {
        try { System.Threading.Thread.Sleep(ms); } catch { }
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
    private struct INPUT
    {
        public uint type;
        public InputUnion u;

        public MOUSEINPUT mi
        {
            get => u.mi;
            set => u.mi = value;
        }

        public KEYBDINPUT ki
        {
            get => u.ki;
            set => u.ki = value;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
