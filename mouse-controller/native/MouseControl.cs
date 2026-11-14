using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DshMouse
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            bool selfTest = false;
            bool debug = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--selftest") selfTest = true;
                else if (args[i] == "--debug") debug = true;
            }
            if (selfTest)
            {
                return SelfTest();
            }
            using (MouseApp app = new MouseApp(debug))
            {
                app.Run();
            }
            return 0;
        }

        static int SelfTest()
        {
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return 2;
            Console.Out.WriteLine("{\"ok\":true,\"cursorX\":" + p.X + ",\"cursorY\":" + p.Y + "}");
            Console.Out.Flush();
            return 0;
        }
    }

    static class Native
    {
        public const uint INPUT_MOUSE = 0;
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);

        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        public static Rectangle VirtualScreen()
        {
            int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (w <= 0) w = 1;
            if (h <= 0) h = 1;
            return new Rectangle(x, y, w, h);
        }

        public static void SendMouse(uint flags, int dx, int dy, uint data)
        {
            INPUT input = new INPUT();
            input.type = INPUT_MOUSE;
            input.mi.dx = dx;
            input.mi.dy = dy;
            input.mi.mouseData = data;
            input.mi.dwFlags = flags;
            input.mi.time = 0;
            input.mi.dwExtraInfo = IntPtr.Zero;
            SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void MoveAbsolute(int x, int y)
        {
            Rectangle vs = VirtualScreen();
            int nx = (int)Math.Round((x - vs.Left) * 65535.0 / Math.Max(1, vs.Width - 1));
            int ny = (int)Math.Round((y - vs.Top) * 65535.0 / Math.Max(1, vs.Height - 1));
            if (nx < 0) nx = 0;
            if (ny < 0) ny = 0;
            if (nx > 65535) nx = 65535;
            if (ny > 65535) ny = 65535;
            SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, nx, ny, 0);
        }
    }

    sealed class OverlayForm : Form
    {
        readonly Label title;
        readonly Label instruction;
        readonly Label step;
        readonly Label hint;
        readonly System.Windows.Forms.Timer pulse;
        bool bright = true;
        bool active = true;
        Color dotColor = Color.FromArgb(255, 64, 64);
        Color accentColor = Color.FromArgb(190, 40, 40);
        const int IdleWidth = 330;
        const int IdleHeight = 62;
        const int TaskWidth = 500;
        const int TaskHeight = 122;

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(330, 62);
            BackColor = Color.FromArgb(24, 24, 27);
            Opacity = 0.94;
            Padding = new Padding(0);

            title = new Label();
            title.Text = "dsh鼠标接管中";
            title.ForeColor = Color.FromArgb(255, 255, 255);
            title.BackColor = Color.Transparent;
            title.Font = new Font("Microsoft YaHei UI", 14.0f, FontStyle.Bold);
            title.AutoSize = false;
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.SetBounds(52, 8, 270, 30);
            Controls.Add(title);

            hint = new Label();
            hint.Text = "按 Ctrl+D+S+H 停止接管";
            hint.ForeColor = Color.FromArgb(200, 200, 205);
            hint.BackColor = Color.Transparent;
            hint.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
            hint.AutoSize = false;
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.SetBounds(52, 36, 270, 20);
            Controls.Add(hint);

            instruction = new Label();
            instruction.Text = "";
            instruction.ForeColor = Color.FromArgb(235, 235, 240);
            instruction.BackColor = Color.Transparent;
            instruction.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
            instruction.AutoSize = false;
            instruction.AutoEllipsis = true;
            instruction.TextAlign = ContentAlignment.MiddleLeft;
            instruction.SetBounds(18, 40, 464, 24);
            instruction.Visible = false;
            Controls.Add(instruction);

            step = new Label();
            step.Text = "";
            step.ForeColor = Color.FromArgb(198, 198, 208);
            step.BackColor = Color.Transparent;
            step.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
            step.AutoSize = false;
            step.AutoEllipsis = true;
            step.TextAlign = ContentAlignment.MiddleLeft;
            step.SetBounds(18, 66, 464, 24);
            step.Visible = false;
            Controls.Add(step);

            pulse = new System.Windows.Forms.Timer();
            pulse.Interval = 420;
            pulse.Tick += delegate { if (active) { bright = !bright; Invalidate(); } };
            pulse.Start();
            SetIdle();
        }

        /** Show the idle "未操控" indicator. */
        public void SetIdle()
        {
            active = false;
            bright = true;
            title.Text = "未操控";
            title.SetBounds(52, 8, 260, 30);
            title.ForeColor = Color.FromArgb(215, 215, 220);
            instruction.Visible = false;
            step.Visible = false;
            hint.Text = "鼠标未被接管";
            hint.ForeColor = Color.FromArgb(140, 140, 148);
            hint.SetBounds(52, 36, 260, 20);
            dotColor = Color.FromArgb(88, 150, 108);
            accentColor = Color.FromArgb(56, 84, 66);
            Size = new Size(IdleWidth, IdleHeight);
            Opacity = 0.82;
            PlaceTopRight();
            Invalidate();
        }

        /** Show the active takeover indicator with the current task and step. */
        public void SetTask(string taskInstruction, string taskStep, int index, int total)
        {
            active = true;
            bright = true;
            title.Text = "dsh鼠标接管中";
            title.SetBounds(52, 6, 430, 28);
            title.ForeColor = Color.FromArgb(255, 255, 255);
            instruction.Visible = true;
            step.Visible = true;
            instruction.Text = "任务：" + (string.IsNullOrEmpty(taskInstruction) ? "鼠标操控" : taskInstruction);
            string progress = (total > 0 && index > 0) ? ("步骤 " + index + "/" + total + "：") : "当前步骤：";
            step.Text = progress + (string.IsNullOrEmpty(taskStep) ? "进行中" : taskStep);
            hint.Text = "按 Ctrl+D+S+H 停止接管";
            hint.ForeColor = Color.FromArgb(170, 170, 178);
            hint.SetBounds(18, 96, 464, 18);
            dotColor = Color.FromArgb(255, 64, 64);
            accentColor = Color.FromArgb(190, 40, 40);
            Size = new Size(TaskWidth, TaskHeight);
            Opacity = 0.94;
            PlaceTopRight();
            Invalidate();
        }

        public bool IsActive
        {
            get { return active; }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000008;   // WS_EX_TOPMOST
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT (click-through)
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Color dot = active
                ? (bright ? dotColor : Color.FromArgb(140, 40, 40))
                : dotColor;
            using (SolidBrush b = new SolidBrush(dot))
            {
                int dotY = active ? 14 : 20;
                g.FillEllipse(b, 18, dotY, 16, 16);
            }
            using (SolidBrush accent = new SolidBrush(accentColor))
            {
                g.FillRectangle(accent, 0, 0, 4, Height);
            }
        }

        public void PlaceTopRight()
        {
            Screen s = Screen.PrimaryScreen;
            Rectangle wa = s.WorkingArea;
            Location = new Point(wa.Right - Width - 20, wa.Top + 16);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                pulse.Stop();
                pulse.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    sealed class KeyboardHook : IDisposable
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_KEYUP = 0x0101;
        const int WM_SYSKEYDOWN = 0x0104;
        const int WM_SYSKEYUP = 0x0105;
        const int VK_CONTROL = 0x11;
        const int VK_LCONTROL = 0xA2;
        const int VK_RCONTROL = 0xA3;

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        readonly LowLevelKeyboardProc proc;
        readonly HashSet<int> down = new HashSet<int>();
        readonly object gate = new object();
        readonly System.Diagnostics.Stopwatch cooldown = System.Diagnostics.Stopwatch.StartNew();
        IntPtr hookId = IntPtr.Zero;

        public event Action ComboPressed;

        public KeyboardHook()
        {
            proc = Callback;
        }

        public bool Installed
        {
            get { return hookId != IntPtr.Zero; }
        }

        public void Install()
        {
            IntPtr module = Native.GetModuleHandle(null);
            hookId = SetWindowsHookEx(WH_KEYBOARD_LL, proc, module, 0);
        }

        public void Uninstall()
        {
            if (hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookId);
                hookId = IntPtr.Zero;
            }
        }

        bool HasCtrl()
        {
            return down.Contains(VK_CONTROL) || down.Contains(VK_LCONTROL) || down.Contains(VK_RCONTROL);
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                int msg = wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                lock (gate)
                {
                    if (isDown) down.Add(k.vkCode);
                    else if (isUp) down.Remove(k.vkCode);
                    if (isDown && down.Contains(0x44) && down.Contains(0x53) && down.Contains(0x48) && HasCtrl())
                    {
                        if (cooldown.ElapsedMilliseconds > 900)
                        {
                            cooldown.Restart();
                            down.Clear();
                            Action handler = ComboPressed;
                            if (handler != null)
                            {
                                try { handler(); } catch { }
                            }
                        }
                    }
                }
            }
            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            Uninstall();
        }
    }

    sealed class MouseApp : IDisposable
    {
        readonly object writeGate = new object();
        readonly object cmdGate = new object();
        readonly Random rng = new Random();
        readonly object rngGate = new object();
        readonly List<string> pendingEvents = new List<string>();
        Form owner;
        OverlayForm overlay;
        KeyboardHook hook;
        Thread reader;
        System.IO.StreamReader inputReader;
        System.IO.StreamWriter outputWriter;
        readonly bool debug;
        volatile bool shutdown;
        volatile bool takeover;
        volatile bool stopped;
        volatile bool abortMove;
        volatile bool overlayVisible;
        volatile bool taskActive;
        string taskInstruction = "";
        string taskStep = "";
        int taskIndex;
        int taskTotal;

        public MouseApp(bool debug)
        {
            this.debug = debug;
        }

        public void Run()
        {
            owner = new Form();
            owner.WindowState = FormWindowState.Minimized;
            owner.ShowInTaskbar = false;
            owner.FormBorderStyle = FormBorderStyle.None;
            owner.Opacity = 0;
            owner.Size = new Size(1, 1);
            owner.StartPosition = FormStartPosition.Manual;
            owner.Location = new Point(-2000, -2000);
            IntPtr handle = owner.Handle; // force handle creation for BeginInvoke

            overlay = new OverlayForm();
            overlay.PlaceTopRight();
            overlay.SetIdle();
            try { overlay.Show(); overlayVisible = true; } catch { }

            try
            {
                inputReader = new System.IO.StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                outputWriter = new System.IO.StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                outputWriter.AutoFlush = true;
                outputWriter.NewLine = "\n";
            }
            catch { }

            reader = new Thread(ReadLoop);
            reader.IsBackground = true;
            reader.Start();

            WriteLine("{\"event\":\"ready\"}");
            Application.Run(new ApplicationContext());

            shutdown = true;
            ReleaseHook();
        }

        bool EnsureHook()
        {
            if (hook != null && hook.Installed) return true;
            ReleaseHook();
            try
            {
                KeyboardHook next = new KeyboardHook();
                next.ComboPressed += OnCombo;
                next.Install();
                if (!next.Installed)
                {
                    next.Dispose();
                    return false;
                }
                hook = next;
                return true;
            }
            catch
            {
                ReleaseHook();
                return false;
            }
        }

        void ReleaseHook()
        {
            if (hook == null) return;
            try { hook.Dispose(); } catch { }
            hook = null;
        }

        bool EnsureHookThreaded()
        {
            try
            {
                if (owner == null || !owner.IsHandleCreated) return false;
                if (owner.InvokeRequired) return (bool)owner.Invoke(new Func<bool>(EnsureHook));
                return EnsureHook();
            }
            catch
            {
                return false;
            }
        }

        void ReleaseHookThreaded()
        {
            try
            {
                if (owner == null || !owner.IsHandleCreated) { ReleaseHook(); return; }
                if (owner.InvokeRequired) owner.Invoke(new Action(ReleaseHook));
                else ReleaseHook();
            }
            catch { }
        }

        string HookUnavailableJson()
        {
            return "{\"ok\":false,\"error\":\"hook-unavailable\",\"message\":\"Cannot install the Ctrl+D+S+H stop hotkey, so the mouse takeover was refused.\"}";
        }

        void OnCombo()
        {
            stopped = true;
            abortMove = true;
            takeover = false;
            taskActive = false;
            try
            {
                if (overlay != null)
                {
                    if (overlay.InvokeRequired)
                    {
                        if (owner != null && owner.IsHandleCreated)
                            owner.BeginInvoke(new Action(SetOverlayIdle));
                    }
                    else
                    {
                        SetOverlayIdle();
                    }
                }
                if (owner != null && owner.IsHandleCreated && owner.InvokeRequired)
                {
                    owner.BeginInvoke(new Action(ReleaseHook));
                }
                else
                {
                    ReleaseHook();
                }
            }
            catch { }
            WriteLine("{\"event\":\"takeover-stopped\",\"reason\":\"hotkey\"}");
        }

        void ReadLoop()
        {
            try
            {
                string line;
                while (!shutdown && (line = (inputReader != null ? inputReader.ReadLine() : Console.In.ReadLine())) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    HandleCommand(line);
                }
            }
            catch (Exception ex)
            {
                WriteLine("{\"event\":\"fatal\",\"error\":\"" + Json.Escape(ex.Message) + "\"}");
            }
            try
            {
                if (owner != null && owner.IsHandleCreated)
                {
                    owner.BeginInvoke(new Action(delegate { Application.ExitThread(); }));
                }
            }
            catch { }
        }

        void HandleCommand(string line)
        {
            string id = "null";
            try
            {
                Dictionary<string, object> cmd = Json.ParseObject(line);
                id = Json.Raw(cmd, "id");
                string name = Json.Str(cmd, "cmd", "");
                if (debug && name == "simulateHotkey")
                {
                    EnsureHookThreaded();
                    taskInstruction = "测试任务：验证停止热键";
                    taskStep = "模拟按住 Ctrl+D+S+H";
                    taskIndex = 1;
                    taskTotal = 1;
                    taskActive = true;
                    takeover = true;
                    ShowOverlayTask();
                    SleepRandom(150, 250);
                    OnCombo();
                    Respond(id, "{\"ok\":true,\"simulated\":true}");
                    return;
                }
                lock (cmdGate)
                {
                    if (name == "shutdown")
                    {
                        Respond(id, "{\"ok\":true}");
                        shutdown = true;
                        ReleaseHookThreaded();
                        try
                        {
                            if (owner != null && owner.IsHandleCreated)
                                owner.BeginInvoke(new Action(delegate { Application.ExitThread(); }));
                        }
                        catch { }
                        return;
                    }
                    if (name == "status")
                    {
                        Respond(id, "{\"ok\":true,\"takeover\":" + Json.Bool(takeover) + ",\"stopped\":" + Json.Bool(stopped)
                            + ",\"overlay\":" + Json.Bool(overlayVisible)
                            + ",\"hook\":" + Json.Bool(hook != null && hook.Installed)
                            + ",\"task\":" + TaskJson()
                            + ",\"cursor\":" + CursorJson() + "}");
                        return;
                    }
                    if (name == "start")
                    {
                        if (!EnsureHookThreaded())
                        {
                            Respond(id, HookUnavailableJson());
                            return;
                        }
                        stopped = false;
                        abortMove = false;
                        BeginTaskFromCommand(cmd);
                        ShowOverlayTask();
                        Respond(id, "{\"ok\":true,\"takeover\":true,\"hook\":true,\"overlay\":" + Json.Bool(overlayVisible)
                            + ",\"task\":" + TaskJson() + "}");
                        return;
                    }
                    if (name == "step")
                    {
                        if (!taskActive)
                        {
                            Respond(id, "{\"ok\":false,\"error\":\"no-active-task\",\"message\":\"No mouse task is in progress; call start with the overall instruction before setting a step.\"}");
                            return;
                        }
                        UpdateTaskStep(cmd);
                        ShowOverlayTask();
                        Respond(id, "{\"ok\":true,\"takeover\":true,\"overlay\":" + Json.Bool(overlayVisible)
                            + ",\"task\":" + TaskJson() + "}");
                        return;
                    }
                    if (name == "done" || name == "stop")
                    {
                        EndTask();
                        abortMove = true;
                        SetOverlayIdle();
                        ReleaseHookThreaded();
                        Respond(id, "{\"ok\":true,\"takeover\":false,\"overlay\":" + Json.Bool(overlayVisible)
                            + ",\"task\":" + TaskJson() + "}");
                        return;
                    }
                    if (stopped)
                    {
                        Respond(id, "{\"ok\":false,\"stopped\":true,\"error\":\"takeover-stopped\",\"message\":\"Mouse takeover was stopped by the user (Ctrl+D+S+H). Ask the user before resuming.\"}");
                        return;
                    }
                    if (name == "move" || name == "click" || name == "drag" || name == "scroll")
                    {
                        if (!EnsureHookThreaded())
                        {
                            Respond(id, HookUnavailableJson());
                            return;
                        }
                        if (!taskActive)
                        {
                            taskInstruction = "鼠标操控";
                            taskStep = "执行 " + name;
                            taskIndex = 0;
                            taskTotal = 0;
                            taskActive = true;
                        }
                        takeover = true;
                        ShowOverlayTask();
                    }
                    if (name == "move")
                    {
                        int x = Json.Int(cmd, "x", -1);
                        int y = Json.Int(cmd, "y", -1);
                        int duration = Json.Int(cmd, "duration", -1);
                        if (x < 0 || y < 0) throw new Exception("move requires x and y screen coordinates");
                        bool ok = MoveHuman(x, y, duration);
                        if (!ok) { Respond(id, StoppedJson()); return; }
                        Respond(id, "{\"ok\":true,\"cursor\":" + CursorJson() + "}");
                        return;
                    }
                    if (name == "click")
                    {
                        int x = Json.Int(cmd, "x", -1);
                        int y = Json.Int(cmd, "y", -1);
                        string button = Json.Str(cmd, "button", "left");
                        int count = Json.Int(cmd, "count", 1);
                        int duration = Json.Int(cmd, "duration", -1);
                        if (count < 1) count = 1;
                        if (count > 3) count = 3;
                        if (x >= 0 && y >= 0)
                        {
                            if (!MoveHuman(x, y, duration)) { Respond(id, StoppedJson()); return; }
                        }
                        for (int i = 0; i < count; i++)
                        {
                            if (stopped || shutdown) { Respond(id, StoppedJson()); return; }
                            SleepRandom(40, 110);
                            ClickButton(button, true);
                            SleepRandom(45, 105);
                            ClickButton(button, false);
                            if (i + 1 < count) SleepRandom(70, 150);
                        }
                        Respond(id, "{\"ok\":true,\"cursor\":" + CursorJson() + ",\"clicked\":" + count + "}");
                        return;
                    }
                    if (name == "drag")
                    {
                        int fx = Json.Int(cmd, "fromX", -1);
                        int fy = Json.Int(cmd, "fromY", -1);
                        int tx = Json.Int(cmd, "toX", -1);
                        int ty = Json.Int(cmd, "toY", -1);
                        string button = Json.Str(cmd, "button", "left");
                        int duration = Json.Int(cmd, "duration", 900);
                        if (fx < 0 || fy < 0 || tx < 0 || ty < 0) throw new Exception("drag requires fromX, fromY, toX and toY");
                        if (!MoveHuman(fx, fy, -1)) { Respond(id, StoppedJson()); return; }
                        SleepRandom(90, 180);
                        ClickButton(button, true);
                        SleepRandom(80, 160);
                        bool ok = MoveHumanPath(tx, ty, duration, true);
                        SleepRandom(70, 150);
                        ClickButton(button, false);
                        if (!ok) { Respond(id, StoppedJson()); return; }
                        Respond(id, "{\"ok\":true,\"cursor\":" + CursorJson() + "}");
                        return;
                    }
                    if (name == "scroll")
                    {
                        int delta = Json.Int(cmd, "delta", 0);
                        int times = Json.Int(cmd, "times", 1);
                        if (times < 1) times = 1;
                        if (times > 10) times = 10;
                        if (delta == 0) throw new Exception("scroll requires a non-zero delta");
                        for (int i = 0; i < times; i++)
                        {
                            if (stopped || shutdown) { Respond(id, StoppedJson()); return; }
                            Native.SendMouse(Native.MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta));
                            SleepRandom(60, 160);
                        }
                        Respond(id, "{\"ok\":true}");
                        return;
                    }
                    Respond(id, "{\"ok\":false,\"error\":\"unknown-cmd\",\"message\":\"Unknown command: " + Json.Escape(name) + "\"}");
                }
            }
            catch (Exception ex)
            {
                Respond(id, "{\"ok\":false,\"error\":\"command-failed\",\"message\":\"" + Json.Escape(ex.Message) + "\"}");
            }
        }

        string StoppedJson()
        {
            return "{\"ok\":false,\"stopped\":true,\"error\":\"takeover-stopped\",\"message\":\"Mouse takeover was stopped by the user (Ctrl+D+S+H). Ask the user before resuming.\"}";
        }

        void ShowOverlayTask()
        {
            try
            {
                if (owner == null || !owner.IsHandleCreated) return;
                if (owner.InvokeRequired)
                {
                    owner.Invoke(new Action(ApplyOverlayTask));
                }
                else
                {
                    ApplyOverlayTask();
                }
            }
            catch { }
        }

        void SetOverlayIdle()
        {
            try
            {
                if (owner == null || !owner.IsHandleCreated) return;
                if (owner.InvokeRequired)
                {
                    owner.Invoke(new Action(ApplyOverlayIdle));
                }
                else
                {
                    ApplyOverlayIdle();
                }
            }
            catch { }
        }

        void ApplyOverlayTask()
        {
            if (overlay == null) return;
            overlay.SetTask(taskInstruction, taskStep, taskIndex, taskTotal);
            if (!overlay.Visible) overlay.Show();
            overlay.TopMost = true;
            overlayVisible = true;
        }

        void ApplyOverlayIdle()
        {
            if (overlay == null) return;
            overlay.SetIdle();
            if (!overlay.Visible) overlay.Show();
            overlay.TopMost = true;
            overlayVisible = true;
        }

        string TaskJson()
        {
            return "{\"active\":" + Json.Bool(taskActive)
                + ",\"instruction\":\"" + Json.Escape(taskInstruction) + "\""
                + ",\"step\":\"" + Json.Escape(taskStep) + "\""
                + ",\"index\":" + taskIndex + ",\"total\":" + taskTotal + "}";
        }

        void BeginTaskFromCommand(Dictionary<string, object> cmd)
        {
            taskInstruction = Json.Str(cmd, "instruction", "鼠标操控任务");
            taskStep = Json.Str(cmd, "step", "准备开始");
            taskIndex = Json.Int(cmd, "index", 0);
            taskTotal = Json.Int(cmd, "total", 0);
            taskActive = true;
            takeover = true;
        }

        void UpdateTaskStep(Dictionary<string, object> cmd)
        {
            taskStep = Json.Str(cmd, "step", taskStep);
            taskIndex = Json.Int(cmd, "index", taskIndex);
            taskTotal = Json.Int(cmd, "total", taskTotal);
        }

        void EndTask()
        {
            taskActive = false;
            takeover = false;
        }

        string CursorJson()
        {
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return "null";
            return "{\"x\":" + p.X + ",\"y\":" + p.Y + "}";
        }

        // ---------- human-like mouse movement ----------

        int Next(int min, int max)
        {
            lock (rngGate) { return rng.Next(min, max); }
        }

        double NextDouble()
        {
            lock (rngGate) { return rng.NextDouble(); }
        }

        void SleepRandom(int minMs, int maxMs)
        {
            int ms = Next(minMs, maxMs + 1);
            int slept = 0;
            while (slept < ms)
            {
                if (stopped || shutdown) return;
                int slice = Math.Min(15, ms - slept);
                Thread.Sleep(slice);
                slept += slice;
            }
        }

        bool MoveHuman(int x, int y, int duration)
        {
            return MoveHumanPath(x, y, duration, false);
        }

        bool MoveHumanPath(int x, int y, int duration, bool isDrag)
        {
            abortMove = false;
            Native.POINT start;
            if (!Native.GetCursorPos(out start)) return false;
            double dx = x - start.X;
            double dy = y - start.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (duration <= 0)
            {
                // Fitts' law flavoured duration: longer trips take longer, never mechanical.
                duration = (int)Math.Round(230 + Math.Min(1200, distance * 0.28) + NextDouble() * 160);
            }
            if (isDrag) duration = (int)(duration * 1.25);
            if (distance < 2)
            {
                Native.MoveAbsolute(x, y);
                return !stopped;
            }

            double angle = Math.Atan2(dy, dx);
            double px = Math.Cos(angle + Math.PI / 2);
            double py = Math.Sin(angle + Math.PI / 2);
            double bow1 = (NextDouble() - 0.5) * distance * 0.22;
            double bow2 = (NextDouble() - 0.5) * distance * 0.22;
            double cx1 = start.X + dx * 0.30 + px * bow1;
            double cy1 = start.Y + dy * 0.30 + py * bow1;
            double cx2 = start.X + dx * 0.72 + px * bow2;
            double cy2 = start.Y + dy * 0.72 + py * bow2;

            int overshoot = 0;
            double targetX = x;
            double targetY = y;
            if (!isDrag && distance > 40 && NextDouble() < 0.35)
            {
                overshoot = Next(2, 7);
                targetX = x + dx / distance * overshoot;
                targetY = y + dy / distance * overshoot;
                // Recompute the curve end for the overshoot target.
                dx = targetX - start.X;
                dy = targetY - start.Y;
            }

            int steps = Math.Max(8, Math.Min(240, duration / 9));
            double noise = Math.Min(2.6, 0.6 + distance * 0.004);
            double jitterPhase = NextDouble() * Math.PI * 2;

            for (int i = 1; i <= steps; i++)
            {
                if (stopped || shutdown) return false;
                if (abortMove) return false;
                double t = (double)i / steps;
                double e = EaseInOut(t);
                // Cubic Bezier along the eased parameter.
                double mt = 1 - e;
                double bx = mt * mt * mt * start.X + 3 * mt * mt * e * cx1 + 3 * mt * e * e * cx2 + e * e * e * targetX;
                double by = mt * mt * mt * start.Y + 3 * mt * mt * e * cy1 + 3 * mt * e * e * cy2 + e * e * e * targetY;
                // Small decaying hand tremor.
                double decay = 1 - e;
                jitterPhase += 0.55;
                double nx = Math.Sin(jitterPhase * 1.7 + NextDouble()) * noise * decay;
                double ny = Math.Cos(jitterPhase * 1.3 + NextDouble()) * noise * decay;
                int mx = (int)Math.Round(bx + nx);
                int my = (int)Math.Round(by + ny);
                if (!isDrag)
                {
                    // Avoid starting a drag from an imprecise point: the first segments are harmless.
                    Native.MoveAbsolute(mx, my);
                }
                else
                {
                    Native.MoveAbsolute(mx, my);
                }
                // Variable interval: slower near the start and end, faster in the middle.
                double speed = 0.35 + Math.Sin(Math.PI * t) * 0.65;
                int wait = (int)Math.Round(7 + (1 - speed) * 9 + NextDouble() * 5);
                Thread.Sleep(wait);
            }

            if (overshoot > 0)
            {
                SleepRandom(35, 90);
                if (!MoveHumanPath(x, y, 160 + Next(0, 90), isDrag)) return false;
            }
            else
            {
                // Final micro correction, as a human settles on the target.
                Native.MoveAbsolute(x, y);
            }
            return !stopped;
        }

        static double EaseInOut(double t)
        {
            // Smooth acceleration then deceleration, slightly asymmetric like a real hand.
            if (t < 0.5) return 4 * t * t * t;
            double f = -2 * t + 2;
            return 1 - f * f * f / 2;
        }

        void ClickButton(string button, bool down)
        {
            if (button == "right")
            {
                Native.SendMouse(down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP, 0, 0, 0);
            }
            else if (button == "middle")
            {
                Native.SendMouse(down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP, 0, 0, 0);
            }
            else
            {
                Native.SendMouse(down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP, 0, 0, 0);
            }
        }

        void Respond(string id, string payload)
        {
            WriteLine("{\"id\":" + id + ",\"result\":" + payload + "}");
        }

        void WriteLine(string line)
        {
            lock (writeGate)
            {
                try
                {
                    if (outputWriter != null)
                    {
                        outputWriter.WriteLine(line);
                        outputWriter.Flush();
                    }
                    else
                    {
                        Console.Out.WriteLine(line);
                        Console.Out.Flush();
                    }
                }
                catch { }
            }
        }

        public void Dispose()
        {
            shutdown = true;
            if (hook != null) hook.Dispose();
            if (overlay != null) overlay.Dispose();
            if (owner != null) owner.Dispose();
            try { if (inputReader != null) inputReader.Dispose(); } catch { }
            try { if (outputWriter != null) outputWriter.Dispose(); } catch { }
        }
    }

    static class Json
    {
        public static Dictionary<string, object> ParseObject(string text)
        {
            int i = 0;
            Dictionary<string, object> result = ParseObjectAt(text, ref i);
            return result;
        }

        static Dictionary<string, object> ParseObjectAt(string s, ref int i)
        {
            Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
            Skip(s, ref i);
            Expect(s, ref i, '{');
            while (true)
            {
                Skip(s, ref i);
                if (Peek(s, i) == '}')
                {
                    i++;
                    break;
                }
                string key = ParseString(s, ref i);
                Skip(s, ref i);
                Expect(s, ref i, ':');
                Skip(s, ref i);
                object value;
                char c = Peek(s, i);
                if (c == '"')
                {
                    value = ParseString(s, ref i);
                }
                else if (c == 't' && Match(s, i, "true"))
                {
                    i += 4;
                    value = true;
                }
                else if (c == 'f' && Match(s, i, "false"))
                {
                    i += 5;
                    value = false;
                }
                else if (c == 'n' && Match(s, i, "null"))
                {
                    i += 4;
                    value = null;
                }
                else
                {
                    int start = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
                    {
                        i++;
                    }
                    value = double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
                }
                map[key] = value;
                Skip(s, ref i);
                char next = Peek(s, i);
                if (next == ',')
                {
                    i++;
                    continue;
                }
                if (next == '}')
                {
                    i++;
                    break;
                }
                throw new FormatException("Malformed JSON object near index " + i);
            }
            return map;
        }

        static char Peek(string s, int i)
        {
            return i < s.Length ? s[i] : '\0';
        }

        static bool Match(string s, int i, string token)
        {
            return i + token.Length <= s.Length && string.CompareOrdinal(s, i, token, 0, token.Length) == 0;
        }

        static void Skip(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static void Expect(string s, ref int i, char c)
        {
            if (Peek(s, i) != c) throw new FormatException("Expected '" + c + "' near index " + i);
            i++;
        }

        static string ParseString(string s, ref int i)
        {
            Expect(s, ref i, '"');
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) break;
                    char e = s[i++];
                    if (e == 'n') sb.Append('\n');
                    else if (e == 't') sb.Append('\t');
                    else if (e == 'r') sb.Append('\r');
                    else if (e == 'b') sb.Append('\b');
                    else if (e == 'f') sb.Append('\f');
                    else if (e == 'u')
                    {
                        string hex = s.Substring(i, 4);
                        i += 4;
                        sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    }
                    else sb.Append(e);
                }
                else
                {
                    sb.Append(c);
                }
            }
            throw new FormatException("Unterminated JSON string");
        }

        public static string Escape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 32) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string Raw(Dictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value) || value == null) return "null";
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is string) return "\"" + Escape((string)value) + "\"";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static string Str(Dictionary<string, object> map, string key, string fallback)
        {
            object value;
            if (map.TryGetValue(key, out value) && value is string) return (string)value;
            return fallback;
        }

        public static int Int(Dictionary<string, object> map, string key, int fallback)
        {
            object value;
            if (map.TryGetValue(key, out value))
            {
                if (value is double)
                {
                    double d = (double)value;
                    if (Math.Abs(d) < int.MaxValue) return (int)Math.Round(d);
                }
            }
            return fallback;
        }

        public static bool Bool(Dictionary<string, object> map, string key, bool fallback)
        {
            object value;
            if (map.TryGetValue(key, out value) && value is bool) return (bool)value;
            return fallback;
        }

        public static string Bool(bool value)
        {
            return value ? "true" : "false";
        }
    }
}
