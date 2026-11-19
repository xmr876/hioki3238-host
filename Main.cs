// -----------------------------------------------------------------------------
//  日置 HIOKI 3237 / 3238 / 3239 上位机
//  作者：蓝色大肥鱼
//  许可：MIT License
//
//  通讯：RS-232C 9600-8-N-1，分隔符 CR+LF，指令集 SCP（日置原厂）
//  读数：:FETC?（读上次测量值，不触发）
//  量程：:VOLT:DC:RANG 1.99999 / :VOLT:DC:RANG:AUTO ON（满量程值、功能节点）
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Hioki3238
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool demo = false;
            bool selftest = false;
            string demoFunc = null;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (a == "--demo") demo = true;
                    if (a == "--selftest") selftest = true;
                    if (a.StartsWith("--func=")) demoFunc = a.Substring(7).ToUpperInvariant();
                }
            }
            MainForm.DemoFunc = demoFunc;

            if (selftest)
            {
                StringBuilder sb = new StringBuilder();
                string[] samples = { "V +1.23424E+00", "+1.23424E+0", "V +001.234E-3",
                                     "1.23456E+00", "OL", "", "OF", "V  +150.0E+0" };
                foreach (string s in samples)
                {
                    double v; string u;
                    bool ok = Meter.TryParse(s, out v, out u);
                    sb.AppendLine(s + " => ok=" + ok + " g6=" +
                                  (ok ? v.ToString("G6", CultureInfo.InvariantCulture) : "-") + " u=" + u);
                }
                try
                {
                    using (MainForm mf = new MainForm(true))
                    {
                        mf.Show();
                        Application.DoEvents();
                        foreach (Control c in mf.Controls[0].Controls) Dump(sb, c, 0);
                        mf.Close();
                    }
                }
                catch (Exception ex) { sb.AppendLine("dump err " + ex.Message); }
                File.WriteAllText("selftest.txt", sb.ToString(), Encoding.UTF8);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(demo));
        }

        static void Dump(StringBuilder sb, Control c, int depth)
        {
            if (depth > 6) return;
            string text = c.Text == null ? "" : c.Text.Replace("\r", " ").Replace("\n", " ");
            if (text.Length > 24) text = text.Substring(0, 24);
            sb.AppendLine(new string(' ', depth * 2) + c.GetType().Name + " [" + text + "] " +
                          c.Left + "," + c.Top + " " + c.Width + "x" + c.Height +
                          (c.Visible ? "" : " HIDDEN"));
            foreach (Control child in c.Controls) Dump(sb, child, depth + 1);
        }
    }

    // ------------------------------------------------------------------
    // 串口
    // ------------------------------------------------------------------
    interface ILink
    {
        bool IsOpen { get; }
        void Open(string port, int baud);
        void Close();
        string Query(string cmd, int timeoutMs);
    }

    class SerialLink : ILink
    {
        SerialPort sp = new SerialPort();
        object sync = new object();

        public bool IsOpen { get { return sp != null && sp.IsOpen; } }

        public void Open(string port, int baud)
        {
            Close();
            sp = new SerialPort(port, baud, Parity.None, 8, StopBits.One);
            sp.Handshake = Handshake.None;
            sp.ReadTimeout = 300;
            sp.WriteTimeout = 800;
            sp.DtrEnable = true;
            sp.RtsEnable = true;
            sp.Open();
            sp.DiscardInBuffer();
            sp.DiscardOutBuffer();
        }

        public void Close()
        {
            try { if (sp != null && sp.IsOpen) sp.Close(); }
            catch { }
        }

        public string Query(string cmd, int timeoutMs)
        {
            lock (sync)
            {
                if (sp == null || !sp.IsOpen) throw new InvalidOperationException("串口未打开");
                sp.DiscardInBuffer();
                sp.Write(cmd + "\r\n");
                StringBuilder sb = new StringBuilder();
                Stopwatch sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    int n = 0;
                    try { n = sp.BytesToRead; }
                    catch { break; }
                    if (n > 0)
                    {
                        byte[] buf = new byte[n];
                        int read = sp.Read(buf, 0, n);
                        sb.Append(Encoding.ASCII.GetString(buf, 0, read));
                        if (sb.ToString().IndexOf('\n') >= 0) break;
                    }
                    else
                    {
                        Thread.Sleep(2);
                    }
                }
                return sb.ToString().Replace("\r", "").Replace("\n", "").Trim();
            }
        }
    }

    // ------------------------------------------------------------------
    // 模拟数据源（启动参数 --demo）
    // ------------------------------------------------------------------
    class FakeLink : ILink
    {
        bool open;
        DateTime t0 = DateTime.Now;

        public bool IsOpen { get { return open; } }

        public void Open(string port, int baud) { open = true; }
        public void Close() { open = false; }

        public string Query(string cmd, int timeoutMs)
        {
            Thread.Sleep(20);
            string c = (cmd ?? "").Trim().ToUpperInvariant();
            if (c == "*IDN?") return "HIOKI,3238,0,V2.00(DEMO)";
            if (c.StartsWith("FUNC?")) return ":SENSE:FUNCTION 'VOLT:DC'";
            if (c.StartsWith(":SAMP:RATE?")) return "SLOW";
            if (c.StartsWith(":RANG:AUTO?")) return "ON";
            if (c.StartsWith(":RANG:AUTO")) return "";
            if (c.EndsWith(":RANG?")) return "1.99999E+00";
            if (c.IndexOf(":RANG ") > 0) return "";
            if (c.StartsWith(":FETC") || c.StartsWith(":READ") || c.StartsWith(":MEAS"))
            {
                double t = (DateTime.Now - t0).TotalSeconds;
                double v = 1.23456 + 0.00123 * Math.Sin(t * 1.5) + 0.00021 * Math.Sin(t * 17.0)
                           + 0.00004 * Math.Sin(t * 61.0);
                return "V " + v.ToString("+0.00000E+00;-0.00000E+00", CultureInfo.InvariantCulture);
            }
            return "";
        }
    }

    // ------------------------------------------------------------------
    // 回复解析
    // ------------------------------------------------------------------
    static class Meter
    {
        static readonly Regex HeaderRe = new Regex(@"^([A-Za-z]{1,6})\s+(.+)$");
        static readonly Regex NumRe = new Regex(@"[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?");

        public static string UnitOfHeader(string header)
        {
            string h = header.ToUpperInvariant();
            if (h.StartsWith("V")) return "V";
            if (h.StartsWith("A")) return "A";
            if (h.StartsWith("OHM") || h.StartsWith("R")) return "Ω";
            if (h.StartsWith("HZ") || h.StartsWith("FREQ")) return "Hz";
            if (h.StartsWith("W")) return "W";
            return "";
        }

        public static bool TryParse(string reply, out double value, out string unit)
        {
            value = 0;
            unit = "";
            if (string.IsNullOrEmpty(reply)) return false;
            string s = reply.Replace("\r", "").Replace("\n", "").Trim();
            foreach (string part in s.Split(';'))
            {
                string item = part.Trim();
                if (item.Length == 0) continue;
                Match hm = HeaderRe.Match(item);
                if (hm.Success)
                {
                    unit = UnitOfHeader(hm.Groups[1].Value);
                    item = hm.Groups[2].Value.Trim();
                }
                Match m = NumRe.Match(item);
                if (!m.Success) continue;
                double v;
                if (double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                {
                    value = v;
                    return true;
                }
            }
            return false;
        }

        public static bool IsOverflow(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return false;
            string s = reply.ToUpperInvariant();
            return s.Contains("OF") || s.Contains("OL") || s.Contains("-----");
        }
    }

    // ------------------------------------------------------------------
    // 测量功能定义（含量程）
    // ------------------------------------------------------------------
    class FuncInfo
    {
        public string Key;
        public string Label;
        public string Cmd;
        public string Unit;
        public string Node;        // SCPI 节点，用于量程指令；空表示无手动量程
        public double[] Ranges;    // 满量程值（基本单位）
        public string[] RangeText; // 显示文本
        public bool HasAuto;       // 是否有 <node>:RANG:AUTO 指令

        public FuncInfo(string key, string label, string cmd, string unit,
                        string node, double[] ranges, string[] rangeText, bool hasAuto)
        {
            Key = key; Label = label; Cmd = cmd; Unit = unit;
            Node = node; Ranges = ranges; RangeText = rangeText; HasAuto = hasAuto;
        }
    }

    // ------------------------------------------------------------------
    // 实时曲线
    // ------------------------------------------------------------------
    class TrendChart : Control
    {
        public List<double> Data = new List<double>();
        public int MaxPoints = 600;
        public string Unit = "V";
        public float ScaleFactor = 1f;

        static readonly Color Bg = Color.FromArgb(17, 22, 28);
        static readonly Color Grid = Color.FromArgb(36, 48, 64);
        static readonly Color Line = Color.FromArgb(77, 255, 158);

        public TrendChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Bg;
        }

        public void Add(double v)
        {
            Data.Add(v);
            if (Data.Count > MaxPoints) Data.RemoveRange(0, Data.Count - MaxPoints);
            Invalidate();
        }

        public void ClearData()
        {
            Data.Clear();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = Width, h = Height;
            if (w < 60 || h < 40) return;

            float s = ScaleFactor;
            using (Font small = new Font("Consolas", 8f))
            using (Font titleFont = new Font("Microsoft YaHei UI", 9f))
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(127, 142, 163)))
            using (SolidBrush bright = new SolidBrush(Line))
            using (Pen gridPen = new Pen(Grid))
            {
                int left = TextRenderer.MeasureText("-000.000", small).Width + (int)(10 * s);
                int right = (int)(10 * s);
                int top = (int)(26 * s);
                int bottom = (int)(20 * s);
                Rectangle plot = new Rectangle(left, top, Math.Max(10, w - left - right),
                                               Math.Max(10, h - top - bottom));

                g.DrawString("实时曲线（按采样点记录波动）", titleFont, dim, left, (int)(6 * s));

                if (Data.Count < 1)
                {
                    using (StringFormat sf = new StringFormat())
                    using (Font wait = new Font("Microsoft YaHei UI", 11f))
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString("等待数据…", wait, dim, plot, sf);
                    }
                    return;
                }

                double lo = Data[0], hi = Data[0];
                for (int i = 1; i < Data.Count; i++)
                {
                    if (Data[i] < lo) lo = Data[i];
                    if (Data[i] > hi) hi = Data[i];
                }
                if (hi - lo < 1e-12)
                {
                    double pad0 = Math.Max(Math.Abs(hi) * 0.01, 1e-9);
                    lo -= pad0; hi += pad0;
                }
                else
                {
                    double pad1 = (hi - lo) * 0.12;
                    lo -= pad1; hi += pad1;
                }

                for (int i = 0; i < 5; i++)
                {
                    int y = plot.Top + plot.Height * i / 4;
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    double val = hi - (hi - lo) * i / 4.0;
                    string text = val.ToString("G6", CultureInfo.InvariantCulture);
                    SizeF sz = g.MeasureString(text, small);
                    g.DrawString(text, small, dim, plot.Left - sz.Width - 4 * s, y - sz.Height / 2);
                }

                int n = Data.Count;
                float denom = Math.Max(n - 1, 1);
                PointF[] pts = new PointF[n];
                for (int i = 0; i < n; i++)
                {
                    float x = plot.Left + (float)(plot.Width * (double)i / denom);
                    float y = plot.Bottom - (float)((Data[i] - lo) / (hi - lo) * plot.Height);
                    pts[i] = new PointF(x, y);
                }

                using (Pen linePen = new Pen(Line, 1.6f * s))
                {
                    if (n >= 2) g.DrawLines(linePen, pts);
                    else g.FillEllipse(bright, pts[0].X - 2 * s, pts[0].Y - 2 * s, 4 * s, 4 * s);
                }

                using (SolidBrush white = new SolidBrush(Color.White))
                    g.FillEllipse(white, pts[n - 1].X - 3 * s, pts[n - 1].Y - 3 * s, 6 * s, 6 * s);

                string cur = Data[Data.Count - 1].ToString("G6", CultureInfo.InvariantCulture) + " " + Unit;
                using (Font curFont = new Font("Consolas", 10f, FontStyle.Bold))
                {
                    SizeF cs = g.MeasureString(cur, curFont);
                    g.DrawString(cur, curFont, bright, plot.Right - cs.Width - 2 * s, (int)(4 * s));
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // 主窗口
    // ------------------------------------------------------------------
    class MainForm : Form
    {
        static readonly FuncInfo[] FUNCS = new FuncInfo[] {
            new FuncInfo("DCV", "直流电压", "FUNC 'VOLT:DC'", "V", "VOLT:DC",
                new double[]{0.199999, 1.99999, 19.9999, 199.999, 1000},
                new string[]{"200mV", "2V", "20V", "200V", "1000V"}, true),
            new FuncInfo("ACV", "交流电压", "FUNC 'VOLT:AC'", "V", "VOLT:AC",
                new double[]{1.99999, 19.9999, 199.999, 750},
                new string[]{"2V", "20V", "200V", "750V"}, true),
            new FuncInfo("DCI", "直流电流", "FUNC 'CURR:DC'", "A", "CURR:DC",
                new double[]{0.199999, 1.99999},
                new string[]{"200mA", "2A"}, true),
            new FuncInfo("ACI", "交流电流", "FUNC 'CURR:AC'", "A", "CURR:AC",
                new double[]{0.199999, 1.99999},
                new string[]{"200mA", "2A"}, true),
            new FuncInfo("RES", "电阻 2W", "FUNC 'RES'", "Ω", "RES",
                new double[]{199.999, 1999.99, 19999.9, 199999, 1999990, 19999900, 100000000},
                new string[]{"200Ω", "2kΩ", "20kΩ", "200kΩ", "2MΩ", "20MΩ", "100MΩ"}, true),
            new FuncInfo("LPR", "低功率电阻", "FUNC 'LPRESISTANCE'", "Ω", "LPR",
                new double[]{1999.99, 19999.9, 199999, 1999990},
                new string[]{"2kΩ", "20kΩ", "200kΩ", "2MΩ"}, true),
            new FuncInfo("DIO", "二极管", "FUNC 'DIODe'", "V", "",
                null, null, false),
            new FuncInfo("CONT", "通断", "FUNC 'CONT'", "Ω", "",
                null, null, false),
            new FuncInfo("FREQ", "频率", "FUNC 'FREQ'", "Hz", "FREQ:VOLT",
                new double[]{1.99999, 19.9999, 199.999, 700},
                new string[]{"2V", "20V", "200V", "700V"}, false),
        };

        static readonly string[][] RATES = new string[][] {
            new string[]{"SLOW",   "SLOW"},
            new string[]{"MED",    "MEDIUM"},
            new string[]{"FAST",   "FAST"},
        };

        static readonly Color ColInfo = Color.FromArgb(139, 152, 168);
        static readonly Color ColRx = Color.FromArgb(142, 240, 168);
        static readonly Color ColErr = Color.FromArgb(255, 123, 123);
        static readonly Color ColSel = Color.FromArgb(198, 228, 255);

        float dip = 1f;
        public static string DemoFunc;
        bool demoMode;
        ILink link;

        ComboBox cboPort, cboBaud;
        Button btnConnect, btnRun;
        Label lblState, lblIdn;
        Label lblValue, lblUnit;
        Button[] funcButtons;
        RadioButton[] rateRadios;
        NumericUpDown numInterval;
        FlowLayoutPanel rangeFlow;
        List<Button> rangeButtons = new List<Button>();
        int currentFunc;
        int currentRange = -1;   // -1 = AUTO

        Label lblN, lblMin, lblMax, lblAvg, lblSd, lblPp, lblRate, lblStep;
        TextBox txtCsv;
        CheckBox chkAuto;
        RichTextBox rtbLog;
        TextBox txtCmd;
        TrendChart chart;
        Label lblStatus, lblHint;

        Thread worker;
        volatile bool running;
        bool suppressRate;
        int intervalMs = 500;

        long count;
        double sum, sum2, vmin, vmax;
        double lastValue, maxStep;
        bool hasLast;
        string currentUnit = "V";
        Queue<DateTime> stamps = new Queue<DateTime>();

        string autoCmd = "";
        StreamWriter csv;
        string csvPath = "";

        int S(int v) { return (int)Math.Round(v * dip); }

        public MainForm(bool demo)
        {
            demoMode = demo;
            link = demo ? (ILink)new FakeLink() : (ILink)new SerialLink();

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                dip = Math.Max(1f, g.DpiX / 96f);

            Text = demo ? "日置 HIOKI 3238 上位机 v2.1  [模拟]" : "日置 HIOKI 3238 上位机 v2.1";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(244, 245, 247);
            KeyPreview = true;
            DoubleBuffered = true;

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            MinimumSize = new Size(
                Math.Min(S(900), Math.Max(S(700), wa.Width - S(40))),
                Math.Min(S(620), Math.Max(S(500), wa.Height - S(50))));

            int w = Math.Min(S(1100), (int)(wa.Width * 0.95));
            int h = Math.Min(S(720), (int)(wa.Height * 0.90));
            ClientSize = new Size(w, h);
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();
            LoadSettings();

            FormClosing += OnClosing;
            KeyDown += OnKeyDown;
            Load += delegate
            {
                chart.ScaleFactor = dip;
                if (demoMode) StartDemo();
            };
        }

        void StartDemo()
        {
            cboPort.Items.Clear();
            cboPort.Items.Add("DEMO");
            cboPort.SelectedIndex = 0;
            ToggleConnect();
            numInterval.Value = 200;
            if (!string.IsNullOrEmpty(DemoFunc))
            {
                for (int i = 0; i < FUNCS.Length; i++)
                {
                    if (FUNCS[i].Key == DemoFunc)
                    {
                        FuncButtonClick(funcButtons[i], EventArgs.Empty);
                        break;
                    }
                }
            }
            StartRun();
        }

        // ---------------- 界面 ----------------
        void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(46)));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(24)));
            Controls.Add(root);

            root.Controls.Add(BuildToolbar(), 0, 0);

            TableLayoutPanel body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.ColumnCount = 2;
            body.RowCount = 1;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(340)));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.Padding = new Padding(S(6), 0, S(6), S(4));

            Panel scroller = new Panel();
            scroller.Dock = DockStyle.Fill;
            scroller.AutoScroll = true;
            TableLayoutPanel leftPanel = (TableLayoutPanel)BuildLeft();
            leftPanel.Dock = DockStyle.Top;
            leftPanel.Height = S(576);
            scroller.Controls.Add(leftPanel);
            body.Controls.Add(scroller, 0, 0);

            body.Controls.Add(BuildRight(), 1, 0);
            root.Controls.Add(body, 0, 1);

            root.Controls.Add(BuildStatus(), 0, 2);
        }

        Control BuildToolbar()
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.RowCount = 1;
            t.ColumnCount = 8;
            t.Padding = new Padding(S(6), S(7), S(6), S(2));
            for (int i = 0; i < 7; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            Label l1 = new Label();
            l1.Text = "串口";
            l1.AutoSize = true;
            l1.Anchor = AnchorStyles.Left;
            l1.Margin = new Padding(0, S(9), S(4), 0);
            t.Controls.Add(l1, 0, 0);

            cboPort = new ComboBox();
            cboPort.DropDownStyle = ComboBoxStyle.DropDownList;
            cboPort.Width = S(88);
            cboPort.Margin = new Padding(0, S(4), S(4), 0);
            t.Controls.Add(cboPort, 1, 0);

            Button bRefresh = new Button();
            bRefresh.Text = "刷新";
            bRefresh.AutoSize = true;
            bRefresh.Margin = new Padding(0, S(3), S(10), 0);
            bRefresh.Click += delegate { RefreshPorts(); };
            t.Controls.Add(bRefresh, 2, 0);

            Label l2 = new Label();
            l2.Text = "波特率";
            l2.AutoSize = true;
            l2.Anchor = AnchorStyles.Left;
            l2.Margin = new Padding(0, S(9), S(4), 0);
            t.Controls.Add(l2, 3, 0);

            cboBaud = new ComboBox();
            cboBaud.DropDownStyle = ComboBoxStyle.DropDownList;
            cboBaud.Width = S(76);
            cboBaud.Items.AddRange(new object[] { "9600", "19200", "38400", "4800" });
            cboBaud.SelectedIndex = 0;
            cboBaud.Margin = new Padding(0, S(4), S(10), 0);
            t.Controls.Add(cboBaud, 4, 0);

            btnConnect = new Button();
            btnConnect.Text = "连接";
            btnConnect.AutoSize = true;
            btnConnect.Margin = new Padding(0, S(3), S(10), 0);
            btnConnect.Click += delegate { ToggleConnect(); };
            t.Controls.Add(btnConnect, 5, 0);

            lblState = new Label();
            lblState.Text = "未连接";
            lblState.AutoSize = true;
            lblState.Anchor = AnchorStyles.Left;
            lblState.ForeColor = Color.FromArgb(107, 114, 128);
            lblState.Margin = new Padding(0, S(9), S(10), 0);
            t.Controls.Add(lblState, 6, 0);

            lblIdn = new Label();
            lblIdn.Text = "设备：—";
            lblIdn.Dock = DockStyle.Fill;
            lblIdn.AutoSize = false;
            lblIdn.TextAlign = ContentAlignment.MiddleRight;
            lblIdn.ForeColor = Color.FromArgb(107, 114, 128);
            lblIdn.Margin = new Padding(0, S(4), 0, 0);
            t.Controls.Add(lblIdn, 7, 0);

            return t;
        }

        Control BuildLeft()
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.ColumnCount = 1;
            t.RowCount = 7;
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(100)));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(182)));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(84)));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(32)));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(100)));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(56)));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // 大字号读数
            Panel readout = new Panel();
            readout.Dock = DockStyle.Fill;
            readout.BackColor = Color.FromArgb(15, 26, 20);
            readout.Margin = new Padding(0, S(2), 0, S(2));

            TableLayoutPanel ro = new TableLayoutPanel();
            ro.Dock = DockStyle.Fill;
            ro.ColumnCount = 2;
            ro.RowCount = 1;
            ro.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ro.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(66)));

            lblValue = new Label();
            lblValue.Text = "-----";
            lblValue.Dock = DockStyle.Fill;
            lblValue.AutoSize = false;
            lblValue.Font = new Font("Consolas", 32f, FontStyle.Bold);
            lblValue.ForeColor = Color.FromArgb(77, 255, 158);
            lblValue.BackColor = Color.FromArgb(15, 26, 20);
            lblValue.TextAlign = ContentAlignment.MiddleRight;
            ro.Controls.Add(lblValue, 0, 0);

            lblUnit = new Label();
            lblUnit.Text = "";
            lblUnit.Dock = DockStyle.Fill;
            lblUnit.AutoSize = false;
            lblUnit.Font = new Font("Consolas", 15f);
            lblUnit.ForeColor = Color.FromArgb(154, 220, 255);
            lblUnit.BackColor = Color.FromArgb(15, 26, 20);
            lblUnit.TextAlign = ContentAlignment.MiddleLeft;
            ro.Controls.Add(lblUnit, 1, 0);

            readout.Controls.Add(ro);
            t.Controls.Add(readout, 0, 0);

            // 测量功能 + 量程
            GroupBox grpFunc = MakeGroup("测量功能 / 量程 (RANGE)");
            TableLayoutPanel fg = new TableLayoutPanel();
            fg.Dock = DockStyle.Fill;
            fg.ColumnCount = 1;
            fg.RowCount = 2;
            fg.RowStyles.Add(new RowStyle(SizeType.Absolute, S(90)));
            fg.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 3;
            grid.RowCount = 3;
            for (int i = 0; i < 3; i++)
            {
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34f));
            }
            funcButtons = new Button[FUNCS.Length];
            for (int i = 0; i < FUNCS.Length; i++)
            {
                Button b = new Button();
                b.Text = FUNCS[i].Label;
                b.Dock = DockStyle.Fill;
                b.Margin = new Padding(S(2));
                b.Tag = i;
                b.UseVisualStyleBackColor = false;
                b.BackColor = SystemColors.Control;
                b.Click += FuncButtonClick;
                grid.Controls.Add(b, i % 3, i / 3);
                funcButtons[i] = b;
            }
            fg.Controls.Add(grid, 0, 0);

            rangeFlow = new FlowLayoutPanel();
            rangeFlow.Dock = DockStyle.Fill;
            rangeFlow.WrapContents = true;
            rangeFlow.AutoScroll = false;
            rangeFlow.Margin = new Padding(0, S(2), 0, 0);
            fg.Controls.Add(rangeFlow, 0, 1);

            grpFunc.Controls.Add(fg);
            t.Controls.Add(grpFunc, 0, 1);

            // 采样速度 + 间隔
            GroupBox grpRate = MakeGroup("采样速度 (SAMPLE RATE) / 间隔");
            TableLayoutPanel rt = new TableLayoutPanel();
            rt.Dock = DockStyle.Fill;
            rt.ColumnCount = 1;
            rt.RowCount = 2;
            rt.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            rt.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            FlowLayoutPanel flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.WrapContents = false;
            rateRadios = new RadioButton[RATES.Length];
            for (int i = 0; i < RATES.Length; i++)
            {
                RadioButton rb = new RadioButton();
                rb.Text = RATES[i][1];
                rb.AutoSize = true;
                rb.Font = new Font("Consolas", 9.5f);
                rb.Margin = new Padding(S(2), S(3), S(10), 0);
                rb.Tag = RATES[i][0];
                rb.CheckedChanged += RateChanged;
                flow.Controls.Add(rb);
                rateRadios[i] = rb;
            }
            rateRadios[0].Checked = true;
            rt.Controls.Add(flow, 0, 0);

            FlowLayoutPanel flow2 = new FlowLayoutPanel();
            flow2.Dock = DockStyle.Fill;
            flow2.WrapContents = false;
            Label lInt = new Label();
            lInt.Text = "间隔(ms)";
            lInt.AutoSize = true;
            lInt.Margin = new Padding(S(2), S(5), S(4), 0);
            flow2.Controls.Add(lInt);
            numInterval = new NumericUpDown();
            numInterval.Minimum = 20;
            numInterval.Maximum = 60000;
            numInterval.Increment = 50;
            numInterval.Value = 500;
            numInterval.Width = S(84);
            numInterval.Margin = new Padding(0, S(2), S(6), 0);
            flow2.Controls.Add(numInterval);
            Label lHint2 = new Label();
            lHint2.Text = "5½位需 SLOW";
            lHint2.AutoSize = true;
            lHint2.ForeColor = Color.FromArgb(107, 114, 128);
            lHint2.Margin = new Padding(0, S(5), 0, 0);
            flow2.Controls.Add(lHint2);
            rt.Controls.Add(flow2, 0, 1);
            grpRate.Controls.Add(rt);
            t.Controls.Add(grpRate, 0, 2);

            // 采集按钮
            TableLayoutPanel btns = new TableLayoutPanel();
            btns.Dock = DockStyle.Fill;
            btns.ColumnCount = 3;
            btns.RowCount = 1;
            btns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            btns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            btns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            btns.Margin = new Padding(0, S(2), 0, S(2));

            Button bOnce = new Button();
            bOnce.Text = "单次 (F5)";
            bOnce.Dock = DockStyle.Fill;
            bOnce.Margin = new Padding(0, 0, S(3), 0);
            bOnce.Click += delegate { ReadOnce(); };
            btns.Controls.Add(bOnce, 0, 0);

            btnRun = new Button();
            btnRun.Text = "连续采集 (F6)";
            btnRun.Dock = DockStyle.Fill;
            btnRun.Margin = new Padding(S(3), 0, S(3), 0);
            btnRun.Click += delegate { ToggleRun(); };
            btns.Controls.Add(btnRun, 1, 0);

            Button bClear = new Button();
            bClear.Text = "清零";
            bClear.Dock = DockStyle.Fill;
            bClear.Margin = new Padding(S(3), 0, 0, 0);
            bClear.Click += delegate { ResetStats(); };
            btns.Controls.Add(bClear, 2, 0);
            t.Controls.Add(btns, 0, 3);

            // 记录
            GroupBox grpCsv = MakeGroup("数据记录 (CSV，每个采样点一行)");
            TableLayoutPanel ct = new TableLayoutPanel();
            ct.Dock = DockStyle.Fill;
            ct.ColumnCount = 2;
            ct.RowCount = 2;
            ct.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ct.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ct.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            ct.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            txtCsv = new TextBox();
            txtCsv.Dock = DockStyle.Fill;
            txtCsv.Margin = new Padding(0, S(1), S(4), S(1));
            ct.Controls.Add(txtCsv, 0, 0);

            Button bBrowse = new Button();
            bBrowse.Text = "…";
            bBrowse.AutoSize = true;
            bBrowse.Margin = new Padding(0, S(1), 0, S(1));
            bBrowse.Click += delegate { ChooseCsv(); };
            ct.Controls.Add(bBrowse, 1, 0);

            chkAuto = new CheckBox();
            chkAuto.Text = "自动记录每次读数";
            chkAuto.AutoSize = true;
            chkAuto.Anchor = AnchorStyles.Left;
            chkAuto.Margin = new Padding(0, S(2), 0, 0);
            chkAuto.CheckedChanged += delegate
            {
                if (chkAuto.Checked)
                {
                    if (csvPath.Length == 0) ChooseCsv();
                    if (csvPath.Length == 0) { chkAuto.Checked = false; return; }
                    EnsureCsv();
                    Log("开始记录到 " + csvPath, ColInfo);
                }
                else
                {
                    CloseCsv();
                }
            };
            ct.Controls.Add(chkAuto, 0, 1);

            Button bDir = new Button();
            bDir.Text = "打开目录";
            bDir.AutoSize = true;
            bDir.Margin = new Padding(0, S(1), 0, S(1));
            bDir.Click += delegate { OpenCsvDir(); };
            ct.Controls.Add(bDir, 1, 1);

            grpCsv.Controls.Add(ct);
            t.Controls.Add(grpCsv, 0, 4);

            // 使用提示
            Label hint = new Label();
            hint.Text = "F5 单次 / F6 连续；5½位需 SLOW\r\n" +
                        "无回复：先发 *IDN? 查线序、9600 8N1\r\n" +
                        "任意指令可在下方控制台直接发送";
            hint.Dock = DockStyle.Fill;
            hint.AutoSize = false;
            hint.Font = new Font("Microsoft YaHei UI", 8f);
            hint.ForeColor = Color.FromArgb(107, 114, 128);
            hint.Margin = new Padding(S(2), S(6), S(2), 0);
            t.Controls.Add(hint, 0, 5);

            RebuildRanges(0, true);
            return t;
        }

        Control BuildRight()
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.ColumnCount = 1;
            t.RowCount = 3;
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(58)));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(158)));

            chart = new TrendChart();
            chart.Dock = DockStyle.Fill;
            chart.Margin = new Padding(0, S(2), 0, S(4));
            t.Controls.Add(chart, 0, 0);

            t.Controls.Add(BuildStatsStrip(), 0, 1);

            GroupBox grpCon = MakeGroup("指令控制台 / 通讯日志");
            TableLayoutPanel ct = new TableLayoutPanel();
            ct.Dock = DockStyle.Fill;
            ct.ColumnCount = 1;
            ct.RowCount = 2;
            ct.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            ct.RowStyles.Add(new RowStyle(SizeType.Absolute, S(30)));

            rtbLog = new RichTextBox();
            rtbLog.Dock = DockStyle.Fill;
            rtbLog.BackColor = Color.FromArgb(15, 18, 22);
            rtbLog.ForeColor = Color.FromArgb(201, 214, 227);
            rtbLog.BorderStyle = BorderStyle.None;
            rtbLog.Font = new Font("Consolas", 9f);
            rtbLog.ReadOnly = true;
            rtbLog.Margin = new Padding(0, 0, 0, S(2));
            ct.Controls.Add(rtbLog, 0, 0);

            FlowLayoutPanel row = new FlowLayoutPanel();
            row.Dock = DockStyle.Fill;
            row.WrapContents = false;
            row.Margin = new Padding(0);

            txtCmd = new TextBox();
            txtCmd.Width = S(200);
            txtCmd.Font = new Font("Consolas", 9f);
            txtCmd.Margin = new Padding(0, S(1), S(4), 0);
            txtCmd.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SendManual(); }
            };
            row.Controls.Add(txtCmd);

            row.Controls.Add(MkButton("发送", delegate { SendManual(); }, S(52)));
            row.Controls.Add(MkButton("*IDN?", delegate { QuickCmd("*IDN?"); }, S(58)));
            row.Controls.Add(MkButton("FUNC?", delegate { QuickCmd("FUNC?"); }, S(62)));
            row.Controls.Add(MkButton("*CLS", delegate { QuickCmd("*CLS"); }, S(58)));
            row.Controls.Add(MkButton("清日志", delegate { rtbLog.Clear(); }, S(60)));

            ct.Controls.Add(row, 0, 1);
            grpCon.Controls.Add(ct);
            t.Controls.Add(grpCon, 0, 2);

            return t;
        }

        Control BuildStatsStrip()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = Color.White;
            p.Margin = new Padding(0, 0, 0, S(4));
            p.Padding = new Padding(S(8), S(4), S(8), S(4));

            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.ColumnCount = 4;
            t.RowCount = 2;
            for (int i = 0; i < 4; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            for (int i = 0; i < 2; i++) t.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            lblN = MkStat(t, 0, 0, "次数  0");
            lblRate = MkStat(t, 1, 0, "采样率  0 Hz");
            lblMin = MkStat(t, 2, 0, "最小  —");
            lblMax = MkStat(t, 3, 0, "最大  —");
            lblAvg = MkStat(t, 0, 1, "平均  —");
            lblSd = MkStat(t, 1, 1, "标准差  —");
            lblPp = MkStat(t, 2, 1, "峰峰值  —");
            lblStep = MkStat(t, 3, 1, "最大跳变  —");

            p.Controls.Add(t);
            return p;
        }

        Label MkStat(TableLayoutPanel t, int col, int row, string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Dock = DockStyle.Fill;
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.BackColor = Color.White;
            l.Margin = new Padding(S(2), 0, S(2), 0);
            t.Controls.Add(l, col, row);
            return l;
        }

        Button MkButton(string text, EventHandler h, int width)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = false;
            b.Width = width;
            b.Height = S(24);
            b.Margin = new Padding(0, S(1), S(4), 0);
            b.Click += h;
            return b;
        }

        GroupBox MakeGroup(string title)
        {
            GroupBox g = new GroupBox();
            g.Text = title;
            g.Dock = DockStyle.Fill;
            g.Padding = new Padding(S(6), S(4), S(6), S(2));
            g.Margin = new Padding(0, S(4), 0, 0);
            return g;
        }

        Control BuildStatus()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;

            lblStatus = new Label();
            lblStatus.Text = "未连接";
            lblStatus.AutoSize = true;
            lblStatus.Location = new Point(S(8), S(4));
            p.Controls.Add(lblStatus);

            lblHint = new Label();
            lblHint.Text = "F5 单次读取    F6 连续采集/停止";
            lblHint.AutoSize = true;
            lblHint.ForeColor = Color.FromArgb(107, 114, 128);
            p.Controls.Add(lblHint);
            p.Resize += delegate
            {
                lblHint.Left = p.Width - lblHint.Width - S(10);
                lblHint.Top = S(4);
            };
            return p;
        }

        // ---------------- 量程 ----------------
        void RebuildRanges(int funcIndex, bool autoSelected)
        {
            rangeFlow.SuspendLayout();
            rangeFlow.Controls.Clear();
            rangeButtons.Clear();

            FuncInfo f = FUNCS[funcIndex];
            if (f.HasAuto) AddRangeButton("AUTO", -1);
            if (f.Ranges != null)
            {
                for (int i = 0; i < f.Ranges.Length; i++)
                    AddRangeButton(f.RangeText[i], i);
            }
            rangeFlow.ResumeLayout();

            if (f.HasAuto && autoSelected) SelectRange(-1);
            else if (f.Ranges != null && f.Ranges.Length > 0) SelectRange(0);
            else SelectRange(-2);
        }

        void AddRangeButton(string text, int index)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = false;
            b.Height = S(22);
            b.Width = Math.Max(S(40), TextRenderer.MeasureText(text, Font).Width + S(10));
            b.Margin = new Padding(0, S(2), S(3), 0);
            b.Tag = index;
            b.UseVisualStyleBackColor = false;
            b.BackColor = SystemColors.Control;
            b.Font = new Font("Microsoft YaHei UI", 8.5f);
            b.Click += RangeButtonClick;
            rangeFlow.Controls.Add(b);
            rangeButtons.Add(b);
        }

        void SelectRange(int index)
        {
            currentRange = index;
            foreach (Button b in rangeButtons)
            {
                int tag = (int)b.Tag;
                b.BackColor = (tag == index) ? ColSel : SystemColors.Control;
            }
        }

        void RangeButtonClick(object sender, EventArgs e)
        {
            Button b = (Button)sender;
            int idx = (int)b.Tag;
            if (!link.IsOpen)
            {
                MessageBox.Show("请先连接串口", "提示");
                SelectRange(idx);
                return;
            }
            FuncInfo f = FUNCS[currentFunc];
            if (f.Node.Length == 0)
            {
                Log("该功能只有单量程，没有远程量程指令", ColInfo);
                return;
            }

            if (idx < 0)
            {
                SelectRange(-1);
                if (!f.HasAuto)
                {
                    Log("该功能没有 RANGe:AUTO 指令（频率只用衰减器量程）", ColInfo);
                    lblStatus.Text = "量程：AUTO（面板）";
                    return;
                }
                string cmdAuto = ":" + f.Node + ":RANG:AUTO ON";
                string r1 = SafeQuery(cmdAuto, 900);
                Log(">>> " + cmdAuto + "\r\n<<< " + Show(r1), r1.Length > 0 ? ColRx : ColInfo);
                string q1 = SafeQuery(":" + f.Node + ":RANG:AUTO?", 900);
                Log(">>> :" + f.Node + ":RANG:AUTO?\r\n<<< " + Show(q1), ColRx);
                lblStatus.Text = "量程：AUTO";
            }
            else
            {
                double v = f.Ranges[idx];
                string cmd = ":" + f.Node + ":RANG " + v.ToString("R", CultureInfo.InvariantCulture);
                string r = SafeQuery(cmd, 900);
                Log(">>> " + cmd + "\r\n<<< " + Show(r), r.Length > 0 ? ColRx : ColInfo);
                SelectRange(idx);
                lblStatus.Text = "量程：" + f.RangeText[idx];
            }
        }

        

        static string Show(string s)
        {
            return string.IsNullOrEmpty(s) ? "(无回复)" : s;
        }

        // ---------------- 串口动作 ----------------
        void RefreshPorts()
        {
            string keep = cboPort.Text;
            cboPort.Items.Clear();
            string[] ports = SerialPort.GetPortNames();
            Array.Sort(ports, delegate(string a, string b)
            {
                int ia = 0, ib = 0;
                int.TryParse(Regex.Replace(a, @"\D", ""), out ia);
                int.TryParse(Regex.Replace(b, @"\D", ""), out ib);
                return ia.CompareTo(ib);
            });
            foreach (string s in ports) cboPort.Items.Add(s);
            if (keep.Length > 0 && cboPort.Items.Contains(keep)) cboPort.SelectedItem = keep;
            else if (cboPort.Items.Count > 0) cboPort.SelectedIndex = 0;
            if (cboPort.Items.Count == 0) Log("没有检测到串口，请检查 USB 转串口驱动", ColErr);
        }

        void ToggleConnect()
        {
            if (link.IsOpen)
            {
                StopRun();
                link.Close();
                btnConnect.Text = "连接";
                lblState.Text = "未连接";
                lblStatus.Text = "未连接";
                return;
            }
            string port = cboPort.Text.Trim();
            if (port.Length == 0) { MessageBox.Show("请选择串口", "提示"); return; }
            int baud = 9600;
            int.TryParse(cboBaud.Text, out baud);
            try
            {
                link.Open(port, baud);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开串口失败：" + ex.Message, "错误");
                Log("打开 " + port + " 失败：" + ex.Message, ColErr);
                return;
            }
            btnConnect.Text = "断开";
            lblState.Text = "已连接 " + port;
            lblStatus.Text = "已连接 " + port + "  " + baud + " 8N1";
            Log("已打开 " + port + "，" + baud + " 8N1", ColInfo);
            Identify();
        }

        void Identify()
        {
            string idn = SafeQuery("*IDN?", 900);
            Log(">>> *IDN?\r\n<<< " + Show(idn), idn.Length > 0 ? ColRx : ColErr);
            lblIdn.Text = idn.Length > 0 ? "设备：" + idn : "设备：无回复（检查线序/设置）";
            SafeQuery(":INIT:CONT ON", 800);   // 连续测量，:FETC? 才能一直取到最新值
            RefreshFuncUi(SafeQuery("FUNC?", 900));
            RefreshRateUi(SafeQuery(":SAMP:RATE?", 900));
        }

        string SafeQuery(string cmd, int timeoutMs)
        {
            try { return link.Query(cmd, timeoutMs); }
            catch (Exception ex) { Log("[错误] " + ex.Message, ColErr); return ""; }
        }

        void RefreshFuncUi(string reply)
        {
            if (reply == null) return;
            string up = reply.ToUpperInvariant();
            up = up.Replace("VOLTAGE", "VOLT").Replace("CURRENT", "CURR")
                   .Replace("RESISTANCE", "RES").Replace("FREQUENCY", "FREQ")
                   .Replace("CONTINUITY", "CONT").Replace("DIODE", "DIO");

            int hitIndex = -1;
            for (int i = 0; i < FUNCS.Length; i++)
            {
                bool hit = false;
                switch (FUNCS[i].Key)
                {
                    case "DCV": hit = up.Contains("VOLT:DC") || up.Contains("VOLT DC"); break;
                    case "ACV": hit = up.Contains("VOLT:AC"); break;
                    case "DCI": hit = up.Contains("CURR:DC"); break;
                    case "ACI": hit = up.Contains("CURR:AC"); break;
                    case "RES": hit = up.Contains("RES") && !up.Contains("LPRES"); break;
                    case "LPR": hit = up.Contains("LPRES"); break;
                    case "DIO": hit = up.Contains("DIO"); break;
                    case "CONT": hit = up.Contains("CONT"); break;
                    case "FREQ": hit = up.Contains("FREQ"); break;
                }
                funcButtons[i].BackColor = hit ? ColSel : SystemColors.Control;
                if (hit) hitIndex = i;
            }
            if (hitIndex >= 0 && hitIndex != currentFunc)
            {
                currentFunc = hitIndex;
                currentUnit = FUNCS[hitIndex].Unit;
                if (lblUnit != null) lblUnit.Text = currentUnit;
                RebuildRanges(hitIndex, true);
            }
        }

        void FuncButtonClick(object sender, EventArgs e)
        {
            if (!link.IsOpen) { MessageBox.Show("请先连接串口", "提示"); return; }
            int idx = (int)((Button)sender).Tag;
            FuncInfo f = FUNCS[idx];
            string reply = SafeQuery(f.Cmd, 900);
            Log(">>> " + f.Cmd + "\r\n<<< " + Show(reply), reply.Length > 0 ? ColRx : ColErr);
            Thread.Sleep(180);
            string q = SafeQuery("FUNC?", 900);
            Log(">>> FUNC?\r\n<<< " + Show(q), ColRx);

            currentFunc = idx;
            currentUnit = f.Unit;
            lblUnit.Text = currentUnit;
            for (int i = 0; i < FUNCS.Length; i++)
                funcButtons[i].BackColor = (i == idx) ? ColSel : SystemColors.Control;

            RebuildRanges(idx, true);
            lblStatus.Text = "功能：" + f.Label + "   量程：" +
                             (currentRange < 0 ? "AUTO" : f.RangeText[currentRange]);
        }

        void RateChanged(object sender, EventArgs e)
        {
            RadioButton rb = sender as RadioButton;
            if (rb == null || !rb.Checked || suppressRate || !link.IsOpen) return;
            string key = (string)rb.Tag;
            string reply = SafeQuery(":SAMP:RATE " + key, 900);
            Log(">>> :SAMP:RATE " + key + "\r\n<<< " + Show(reply), reply.Length > 0 ? ColRx : ColErr);
            string q = SafeQuery(":SAMP:RATE?", 900);
            Log(">>> :SAMP:RATE?\r\n<<< " + q, ColRx);
            RefreshRateUi(q);
            lblStatus.Text = key == "SLOW"
                ? "采样速度：SLOW（5½位）"
                : "采样速度：" + key + "（分辨率会降低）";
        }

        void RefreshRateUi(string reply)
        {
            if (reply == null) return;
            string up = reply.ToUpperInvariant();
            suppressRate = true;
            for (int i = 0; i < RATES.Length; i++)
            {
                string key = RATES[i][0];
                bool hit = key == "SLOW" ? up.Contains("SLOW")
                         : key == "MED" ? up.Contains("MED")
                         : up.Contains("FAST");
                if (hit) { rateRadios[i].Checked = true; break; }
            }
            suppressRate = false;
        }

        // ---------------- 采集 ----------------
        void ReadOnce()
        {
            if (!link.IsOpen) { MessageBox.Show("请先连接串口", "提示"); return; }
            string reply = SafeQuery(":FETC?", 1500);
            Log(">>> :FETC?\r\n<<< " + Show(reply), reply.Length > 0 ? ColRx : ColErr);
            HandleSample(reply);
        }

        void HandleSample(string reply)
        {
            double v;
            string unit;
            if (!Meter.TryParse(reply, out v, out unit))
            {
                if (Meter.IsOverflow(reply))
                {
                    lblValue.Text = "OF";
                    lblStatus.Text = "超量程 / 无读数（OF）";
                }
                else
                {
                    lblStatus.Text = "无有效数值：" + (reply.Length > 0 ? reply : "(超时)");
                }
                return;
            }
            if (unit.Length == 0) unit = currentUnit;
            currentUnit = unit;

            lblValue.Text = FormatReading(v);
            lblUnit.Text = unit;
            chart.Unit = unit;
            chart.Add(v);

            if (count == 0) { vmin = v; vmax = v; }
            count++;
            sum += v;
            sum2 += v * v;
            if (v < vmin) vmin = v;
            if (v > vmax) vmax = v;
            if (hasLast)
            {
                double step = Math.Abs(v - lastValue);
                if (step > maxStep) maxStep = step;
            }
            lastValue = v;
            hasLast = true;

            double avg = sum / count;
            double sd = count > 1 ? Math.Sqrt(Math.Max(0, sum2 / count - avg * avg)) : 0;

            stamps.Enqueue(DateTime.Now);
            while (stamps.Count > 0 && (DateTime.Now - stamps.Peek()).TotalSeconds > 2.0)
                stamps.Dequeue();

            lblN.Text = "次数  " + count;
            lblRate.Text = "采样率  " + (stamps.Count / 2.0).ToString("F1") + " Hz";
            lblMin.Text = "最小  " + Fmt(vmin);
            lblMax.Text = "最大  " + Fmt(vmax);
            lblAvg.Text = "平均  " + Fmt(avg);
            lblSd.Text = "标准差  " + Fmt(sd);
            lblPp.Text = "峰峰值  " + Fmt(vmax - vmin);
            lblStep.Text = "最大跳变  " + Fmt(maxStep);
            lblStatus.Text = "读取正常  " + DateTime.Now.ToString("HH:mm:ss") +
                             "   峰峰 " + Fmt(vmax - vmin) + " " + unit;

            if (chkAuto.Checked) AppendCsv(v, unit, reply);
        }

        static string Fmt(double v)
        {
            return v.ToString("G6", CultureInfo.InvariantCulture);
        }

        /// <summary>按 5½ 位（199999 计数）习惯显示：固定 6 位有效数字，不丢末尾的 0</summary>
        static string FormatReading(double v)
        {
            if (v == 0) return "0.00000";
            double a = Math.Abs(v);
            int e = (int)Math.Floor(Math.Log10(a));
            if (e >= -4 && e <= 5)
            {
                int dec = Math.Max(0, 5 - e);
                return v.ToString("F" + dec.ToString(CultureInfo.InvariantCulture),
                                  CultureInfo.InvariantCulture);
            }
            return v.ToString("0.00000E+00", CultureInfo.InvariantCulture);
        }

        void ResetStats()
        {
            count = 0;
            sum = 0;
            sum2 = 0;
            vmin = 0;
            vmax = 0;
            lastValue = 0;
            maxStep = 0;
            hasLast = false;
            stamps.Clear();
            chart.ClearData();
            lblValue.Text = "-----";
            lblN.Text = "次数  0";
            lblRate.Text = "采样率  0 Hz";
            lblMin.Text = "最小  —";
            lblMax.Text = "最大  —";
            lblAvg.Text = "平均  —";
            lblSd.Text = "标准差  —";
            lblPp.Text = "峰峰值  —";
            lblStep.Text = "最大跳变  —";
        }

        void StartRun()
        {
            if (!link.IsOpen) { MessageBox.Show("请先连接串口", "提示"); return; }
            if (running) return;
            if (!int.TryParse(numInterval.Text, out intervalMs) || intervalMs < 20) intervalMs = 500;
            running = true;
            worker = new Thread(RunLoop);
            worker.IsBackground = true;
            worker.Start();
            btnRun.Text = "停止 (F6)";
            lblStatus.Text = "连续采集中…（" + intervalMs + " ms/点）";
        }

        void StopRun()
        {
            running = false;
            if (worker != null)
            {
                try { worker.Join(1500); } catch { }
                worker = null;
            }
            if (btnRun != null) btnRun.Text = "连续采集 (F6)";
            if (link.IsOpen) lblStatus.Text = "已停止采集";
        }

        void RunLoop()
        {
            while (running)
            {
                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    string reply = link.Query(":FETC?", 1500);
                    if (!running) break;
                    try { BeginInvoke(new Action<string>(HandleSample), reply); }
                    catch (InvalidOperationException) { break; }
                }
                catch (Exception ex)
                {
                    try
                    {
                        BeginInvoke(new Action<string>(delegate(string m) { Log("[错误] " + m, ColErr); }),
                                    ex.Message);
                    }
                    catch { }
                    Thread.Sleep(Math.Max(300, intervalMs));
                    continue;
                }
                int wait = intervalMs - (int)sw.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep(wait);
            }
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; ReadOnce(); }
            if (e.KeyCode == Keys.F6) { e.SuppressKeyPress = true; ToggleRun(); }
        }

        void ToggleRun()
        {
            if (running) StopRun(); else StartRun();
        }

        // ---------------- CSV ----------------
        void ChooseCsv()
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "选择 CSV 保存位置";
                dlg.Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*";
                dlg.FileName = "hioki3238_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (Directory.Exists(dir)) dlg.InitialDirectory = dir;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    csvPath = dlg.FileName;
                    txtCsv.Text = csvPath;
                    CloseCsv();
                }
            }
        }

        void EnsureCsv()
        {
            if (csv != null) return;
            if (csvPath.Length == 0) return;
            bool isNew = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
            csv = new StreamWriter(csvPath, true, new UTF8Encoding(true));
            if (isNew) csv.WriteLine("时间,数值,单位,原始回复");
        }

        void AppendCsv(double v, string unit, string raw)
        {
            try
            {
                EnsureCsv();
                if (csv == null) return;
                csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss.fff},{1:G10},{2},\"{3}\"",
                    DateTime.Now, v, unit, (raw ?? "").Replace("\"", "'")));
                csv.Flush();
            }
            catch (Exception ex)
            {
                chkAuto.Checked = false;
                Log("[CSV 错误] " + ex.Message, ColErr);
            }
        }

        void CloseCsv()
        {
            if (csv == null) return;
            try { csv.Close(); } catch { }
            csv = null;
        }

        void OpenCsvDir()
        {
            string dir = csvPath.Length > 0 ? Path.GetDirectoryName(csvPath)
                                            : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            try { Process.Start("explorer.exe", dir); }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        // ---------------- 控制台 ----------------
        void SendManual()
        {
            string cmd = txtCmd.Text.Trim();
            if (cmd.Length == 0) return;
            if (!link.IsOpen) { Log("[提示] 请先连接串口", ColErr); return; }
            string reply = SafeQuery(cmd, 1500);
            Log(">>> " + cmd + "\r\n<<< " + Show(reply), reply.Length > 0 ? ColRx : ColErr);
            txtCmd.Clear();
        }

        void QuickCmd(string cmd)
        {
            if (!link.IsOpen) { Log("[提示] 请先连接串口", ColErr); return; }
            string reply = SafeQuery(cmd, 1200);
            Log(">>> " + cmd + "\r\n<<< " + Show(reply), reply.Length > 0 ? ColRx : ColErr);
        }

        void Log(string text, Color color)
        {
            if (rtbLog == null || rtbLog.IsDisposed) return;
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor = color;
            rtbLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + "\r\n");
            rtbLog.ScrollToCaret();
        }

        // ---------------- 设置 ----------------
        string SettingsPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                          "Hioki3238");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "config.ini");
            }
        }

        void LoadSettings()
        {
            RefreshPorts();
            cboBaud.SelectedIndex = 0;
            try
            {
                if (File.Exists(SettingsPath))
                {
                    foreach (string line in File.ReadAllLines(SettingsPath))
                    {
                        int p = line.IndexOf('=');
                        if (p <= 0) continue;
                        string k = line.Substring(0, p).Trim();
                        string v = line.Substring(p + 1).Trim();
                        if (k == "port" && v.Length > 0 && cboPort.Items.Contains(v)) cboPort.SelectedItem = v;
                        if (k == "baud" && v.Length > 0) cboBaud.SelectedItem = v;
                        if (k == "interval" && v.Length > 0) numInterval.Text = v;
                        if (k == "csv") { csvPath = v; txtCsv.Text = v; }
                        if (k == "autocmd") autoCmd = v;
                    }
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("port=" + cboPort.Text);
                sb.AppendLine("baud=" + cboBaud.Text);
                sb.AppendLine("interval=" + numInterval.Text);
                sb.AppendLine("csv=" + csvPath);
                sb.AppendLine("# 自动量程远程命令（留空=不发送，由面板 AUTO 键控制）");
                sb.AppendLine("autocmd=" + autoCmd);
                File.WriteAllText(SettingsPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                running = false;
                if (worker != null) worker.Join(800);
                link.Close();
                CloseCsv();
                SaveSettings();
            }
            catch { }
        }
    }
}
