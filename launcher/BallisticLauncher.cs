using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

public sealed class PlayerProfile
{
    // Fixed Goldberg ports: the dedicated server always owns 47584 and every client uses 47600,
    // so start order on the host PC never swaps them. Discovery targets these ports explicitly.
    public const int ServerNetPort = 47584, ClientNetPort = 47600;
    public string Name = "JogadorLAN", Id, Host = "127.0.0.1", Peers = "";
    public int QueryPort = 28016;
    public static string NewId()
    {
        byte[] bytes = new byte[4];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        uint account = BitConverter.ToUInt32(bytes, 0);
        if (account == 0) account = 1;
        return (76561197960265728UL + account).ToString();
    }
    public static PlayerProfile Load(string root)
    {
        var p = new PlayerProfile { Id = NewId() };
        string path = Path.Combine(root, "launcher.local.xml");
        if (File.Exists(path))
        {
            var x = XElement.Load(path);
            p.Name = (string)x.Element("name") ?? p.Name;
            string id = (string)x.Element("id");
            ulong parsed;
            if (UInt64.TryParse(id, out parsed) && parsed > 76561197960265728UL && parsed <= 76561202255233023UL) p.Id = id;
            p.Host = (string)x.Element("host") ?? p.Host;
            p.Peers = (string)x.Element("peers") ?? p.Peers;
            int port;
            if (Int32.TryParse((string)x.Element("queryPort"), out port) && port > 0 && port <= 65535) p.QueryPort = port;
        }
        else if (Directory.Exists(Path.Combine(root, "PerfilLAN")))
        {
            // Adopt the host's tested local identity. This directory is excluded from the shared ZIP.
            string ini = Path.Combine(root, "BallisticOverkill_Data", "Plugins", "steam_settings", "configs.user.ini");
            if (File.Exists(ini))
            {
                string text = File.ReadAllText(ini);
                Match id = Regex.Match(text, @"(?m)^account_steamid=(\d+)\s*$");
                ulong parsed;
                if (id.Success && UInt64.TryParse(id.Groups[1].Value, out parsed) && parsed > 76561197960265728UL && parsed <= 76561202255233023UL) p.Id = id.Groups[1].Value;
            }
        }
        return p;
    }
    // Splits "address" or "address:port" and validates both parts.
    public static string SplitAddress(string raw, int defaultPort, out int port)
    {
        string address = raw.Trim();
        port = defaultPort;
        int colon = address.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!Int32.TryParse(address.Substring(colon + 1), out port) || port <= 0 || port > 65535)
                throw new ArgumentException("Porta inválida em: " + raw + ".");
            address = address.Substring(0, colon);
        }
        IPAddress ip;
        bool validIp = IPAddress.TryParse(address, out ip) && ip.AddressFamily == AddressFamily.InterNetwork;
        bool validDns = Uri.CheckHostName(address) == UriHostNameType.Dns && address.IndexOf('.') >= 0;
        if (!validIp && !validDns) throw new ArgumentException("Endereço inválido: " + raw + ". Use IPv4 ou nome de domínio (porta opcional).");
        return address;
    }
    // Goldberg discovery targets: the local server, the configured server and optional other players, each with an explicit port.
    public static string[] ParseAddresses(string host, string peers)
    {
        if (String.IsNullOrWhiteSpace(host)) throw new ArgumentException("Informe o endereço do servidor.");
        var addresses = new List<string> { "127.0.0.1:" + ServerNetPort };
        int port;
        string server = SplitAddress(host, ServerNetPort, out port) + ":" + port;
        if (!addresses.Contains(server)) addresses.Add(server);
        foreach (string raw in peers.Split(new[] { ';', ',', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string peer = SplitAddress(raw, ClientNetPort, out port) + ":" + port;
            if (!addresses.Contains(peer)) addresses.Add(peer);
        }
        return addresses.ToArray();
    }
    public void Save(string root)
    {
        if (String.IsNullOrWhiteSpace(Name) || Name.Length > 32 || Name.IndexOfAny(new[] { '\r', '\n', '=' }) >= 0)
            throw new ArgumentException("Use um apelido de 1 a 32 caracteres, sem quebras de linha ou sinal de igual.");
        string[] addresses = ParseAddresses(Host, Peers);
        string settings = Path.Combine(root, "BallisticOverkill_Data", "Plugins", "steam_settings");
        Directory.CreateDirectory(settings);
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(Path.Combine(settings, "configs.user.ini"),
            "[user::general]\naccount_name=" + Name.Trim() + "\naccount_steamid=" + Id + "\nlanguage=english\n\n[user::saves]\nlocal_save_path=../../PerfilLAN\n", utf8);
        File.WriteAllText(Path.Combine(settings, "configs.main.ini"),
            "[main::general]\nnew_app_ticket=1\ngc_token=1\n\n[main::connectivity]\ndisable_networking=0\nlisten_port=" + ClientNetPort + "\noffline=0\ndisable_source_query=0\n", utf8);
        File.WriteAllLines(Path.Combine(settings, "custom_broadcasts.txt"), addresses, utf8);
        new XElement("player", new XElement("name", Name.Trim()), new XElement("id", Id), new XElement("host", Host.Trim()),
            new XElement("peers", Peers), new XElement("queryPort", QueryPort)).Save(Path.Combine(root, "launcher.local.xml"));
    }
}

// "Unlock all": the game rebuilds every unlock from each class's XP stat on startup
// (ProgressionService.ValidateAccountLevelUpPrizes), so writing max XP into the emulator's
// stat files is enough. Goldberg keeps one 4-byte int per stat in PerfilLAN\296300\stats.
public static class Progress
{
    public const int MaxClassXp = 1241350; // level 100 (gold class); every unlock ends at level 18
    public static readonly string[] Classes = { "berserker", "grenadier", "marksman", "shadow", "tank", "vanguard", "wraith" };
    public static string UnlockAll(string root)
    {
        string profile = Path.Combine(root, "PerfilLAN", "296300");
        string backup = null;
        if (Directory.Exists(profile))
        {
            backup = Path.Combine(root, "PerfilLAN", "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            CopyDirectory(profile, backup);
        }
        string stats = Path.Combine(profile, "stats");
        Directory.CreateDirectory(stats);
        foreach (string hero in Classes)
        {
            string file = Path.Combine(stats, hero + "_xp_v1");
            int current = 0;
            if (File.Exists(file)) { byte[] b = File.ReadAllBytes(file); if (b.Length == 4) current = BitConverter.ToInt32(b, 0); }
            if (current < MaxClassXp) File.WriteAllBytes(file, BitConverter.GetBytes(MaxClassXp));
        }
        return backup;
    }
    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (string d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }
}

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(12, 14, 17), Bar = Color.FromArgb(9, 10, 13), Panel = Color.FromArgb(17, 20, 25),
        Field = Color.FromArgb(28, 33, 41), Line = Color.FromArgb(38, 44, 54), Text = Color.FromArgb(236, 239, 244),
        Muted = Color.FromArgb(140, 150, 165), Accent = Color.FromArgb(226, 35, 58), Ok = Color.FromArgb(70, 200, 120);
    public static readonly string IconFont = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    static bool FontExists(string name) { using (var f = new Font(name, 10)) return f.Name == name; }
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); float d = radius * 2;
        if (radius <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
    }
}

// Flat owner-drawn button with optional icon glyph; transparent corners show whatever is behind it.
public class FlatButton : Control
{
    public Color Fill = Theme.Field, HoverFill = Color.FromArgb(40, 47, 58), Ink = Theme.Text;
    public string Glyph = "";
    public float Radius = 6;
    bool hover, down;
    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent; Cursor = Cursors.Hand; Font = new Font("Segoe UI Semibold", 10.5f);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        Color fill = !Enabled ? Color.FromArgb((Fill.R + Theme.Panel.R) / 2, (Fill.G + Theme.Panel.G) / 2, (Fill.B + Theme.Panel.B) / 2) : (down ? ControlPaint.Dark(hover ? HoverFill : Fill, 0.05f) : (hover ? HoverFill : Fill));
        using (var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Radius)) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        Color ink = Enabled ? Ink : Color.FromArgb(150, Ink);
        if (Glyph.Length > 0 && Text.Length == 0)
        {
            using (var icon = new Font(Theme.IconFont, Font.Size + 1)) TextRenderer.DrawText(g, Glyph, icon, ClientRectangle, ink, flags | TextFormatFlags.HorizontalCenter);
            return;
        }
        using (var icon = new Font(Theme.IconFont, Font.Size - 0.5f))
        {
            int gw = Glyph.Length > 0 ? TextRenderer.MeasureText(g, Glyph, icon, Size.Empty, flags).Width + 8 : 0;
            int tw = TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags).Width;
            int x = (Width - gw - tw) / 2;
            if (gw > 0) TextRenderer.DrawText(g, Glyph, icon, new Rectangle(x, 0, gw, Height), ink, flags);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + gw, 0, tw + 4, Height), ink, flags);
        }
    }
}

public sealed class LauncherForm : Form
{
    const int TitleH = 40, RailW = 64, PanelW = 360;
    // Glass panel: panel-colored overlay alpha from the top-left (almost opaque) to the bottom-right (see-through).
    const int GlassAlphaTopLeft = 248, GlassAlphaBottomRight = 60;
    readonly string root = AppDomain.CurrentDomain.BaseDirectory;
    readonly PlayerProfile profile;
    readonly string serverExe;
    readonly TextBox name = new TextBox(), host = new TextBox(), peers = new TextBox();
    readonly NumericUpDown port = new NumericUpDown();
    readonly Label panelStatus = new Label();
    readonly FlatButton play = new FlatButton(), settings = new FlatButton(), query = new FlatButton(), server = new FlatButton(), unlock = new FlatButton(), gear = new FlatButton();
    readonly Panel rail = new BufferedPanel(), titleBar = new BufferedPanel();
    readonly GlassPanel panel = new GlassPanel();
    readonly Timer hoverPoll = new Timer { Interval = 40 }, slide = new Timer { Interval = 10 }, serverPoll = new Timer { Interval = 2000 }, liveQuery = new Timer { Interval = 15000 };
    readonly Font statusFont = new Font("Segoe UI", 10.5f);
    const double SlideMs = 180;
    int slideFrom, slideTo = RailW - PanelW;
    DateTime slideStart;
    Bitmap logo;
    bool pinned, querying;
    string liveText = "Consultando servidor…";
    Color liveColor = Theme.Muted;

    public LauncherForm()
    {
        profile = PlayerProfile.Load(root);
        presets = ServerList.Load(root);
        string candidate = Path.GetFullPath(Path.Combine(root, "..", "BallisticServerLAN", "BODS.exe"));
        serverExe = File.Exists(candidate) ? candidate : null;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Text = "Ballistic";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1120, 640);
        BackColor = Theme.Bg; ForeColor = Theme.Text; Font = new Font("Segoe UI", 10);
        DoubleBuffered = true;
        logo = LoadResource("logo.png");
        BuildBackdrop();
        BuildTitleBar();
        BuildRail();
        BuildPanel();
        BuildMain();
        rail.BringToFront(); titleBar.BringToFront();
        hoverPoll.Tick += delegate { UpdatePanel(); }; hoverPoll.Start();
        slide.Tick += delegate { SlideStep(); };
        liveQuery.Tick += delegate { RefreshLiveStatus(); }; liveQuery.Start();
        Shown += delegate { ActiveControl = play; RefreshLiveStatus(); CheckForUpdates(); };
        MouseDown += delegate { if (panel.ContainsFocus) play.Focus(); };
    }

    // ---------- window chrome ----------
    protected override CreateParams CreateParams
    { get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; /* CS_DROPSHADOW */ cp.Style |= 0x20000; /* WS_MINIMIZEBOX: taskbar minimize */ return cp; } }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ReleaseCapture();
    // Default Windows timer granularity is ~15.6 ms, which turns a 25-33 ms interval into an uneven 31/47 ms cadence.
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
    void StopDrift() { if (drift.Enabled) { drift.Stop(); timeEndPeriod(1); } }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
    }
    void DragWindow(object sender, MouseEventArgs e)
    { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } }

    // Game art (Steam key art, logo from the game atlas) is NOT in the public repo/exe: it ships only in the private
    // client package, in <client>\launcher\. Without it the launcher falls back to a plain dark background.
    static Bitmap LoadResource(string name)
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher", name);
        if (!File.Exists(path)) return null;
        using (var s = File.OpenRead(path)) using (var b = new Bitmap(s)) return new Bitmap(b); // copy so the file isn't locked
    }

    // Two pre-rendered layers: the art (slightly oversized so it can drift without showing its edges) and a static
    // overlay with the dim, fades, caption and logo. Each frame only moves the art and composites the overlay on top.
    const float DriftMargin = 0.06f;                 // art is rendered 6% larger than its framed size
    const float DriftX = 18f, DriftY = 0f;           // horizontal-only drift (vertical half-pixel steps looked choppy)
    const int PhasesX = 8, PhasesY = 1;             // 1/8 px horizontal sub-pixel copies
    Bitmap[,] artPhases;
    Bitmap artLayer, overlay, frame, labels, glass;
    Point labelsAt;
    int driftTicks;
    float artBaseX, artBaseY;
    readonly Timer drift = new Timer { Interval = 25 }; // ~40 fps with timeBeginPeriod(1) for a steady cadence
    readonly Stopwatch driftClock = Stopwatch.StartNew();

    void BuildBackdrop()
    {
        int w = ClientSize.Width, h = ClientSize.Height - TitleH;
        float artBottom = h;
        using (var art = LoadResource("fundo.jpg"))
        {
            if (art != null)
            {
                // The art is ~3:1, so covering the full height crops the side characters. Scale it to ~86% of the
                // height, center it right of the rail, pin it to the top and fade its bottom.
                float scale = Math.Max(h * 0.86f / art.Height, (w - RailW) * 1.05f / art.Width);
                float aw = art.Width * scale, ah = art.Height * scale;
                artBottom = ah;
                int lw = (int)Math.Ceiling(aw * (1 + DriftMargin)), lh = (int)Math.Ceiling(ah * (1 + DriftMargin));
                // Resampling per frame costs ~14 ms in GDI+, a plain blit ~0.4 ms. So pre-render the art at every
                // sub-pixel phase and per frame just blit the matching copy. Two steps: scale once with bicubic at an
                // integer position, then shift with plain Bilinear + translate. GDI+'s HighQuality* modes are NOT
                // monotonic for sub-pixel offsets (the edge jumps back at 0.5 px), which made the drift wobble.
                artPhases = new Bitmap[PhasesX, PhasesY];
                using (var scaled = new Bitmap(lw, lh, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                {
                    using (var g = Graphics.FromImage(scaled)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(art, 0, 0, lw, lh); }
                    for (int px = 0; px < PhasesX; px++)
                        for (int py = 0; py < PhasesY; py++)
                        {
                            var b = new Bitmap(lw + 1, lh + 1, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                            using (var g = Graphics.FromImage(b))
                            {
                                g.InterpolationMode = InterpolationMode.Bilinear; g.PixelOffsetMode = PixelOffsetMode.Half;
                                g.TranslateTransform((float)px / PhasesX, (float)py / PhasesY);
                                g.DrawImage(scaled, 0, 0, lw, lh);
                            }
                            artPhases[px, py] = b;
                        }
                }
                artLayer = artPhases[0, 0];
                artBaseX = RailW + (w - RailW - lw) / 2f;
                artBaseY = -(lh - ah) / 2f; // extra margin split above/below the framed position
            }
        }
        overlay = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(overlay))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var dim = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillRectangle(dim, 0, 0, w, artBottom);
            // Gradient brushes tile: fill exactly inside the brush rectangle (inflated by 2px) or the wrap draws a 1px seam.
            var left = new RectangleF(0, 0, w * 0.55f, h);
            using (var b = new LinearGradientBrush(RectangleF.Inflate(left, 2, 0), Color.FromArgb(255, Theme.Bg), Color.FromArgb(0, Theme.Bg), 0f))
            {
                var blend = new ColorBlend(4);
                blend.Colors = new[] { Color.FromArgb(245, Theme.Bg), Color.FromArgb(200, Theme.Bg), Color.FromArgb(80, Theme.Bg), Color.FromArgb(0, Theme.Bg) };
                blend.Positions = new[] { 0f, 0.25f, 0.65f, 1f };
                b.InterpolationColors = blend;
                g.FillRectangle(b, left);
            }
            var bottom = new RectangleF(0, artBottom * 0.45f, w, artBottom * 0.55f);
            using (var b = new LinearGradientBrush(RectangleF.Inflate(bottom, 0, 2), Color.FromArgb(0, Theme.Bg), Color.FromArgb(255, Theme.Bg), 90f))
                g.FillRectangle(b, bottom);
            using (var solid = new SolidBrush(Theme.Bg)) g.FillRectangle(solid, 0, artBottom - 1, w, h - artBottom + 1);
        }
        {
            int x = RailW + 56, full = ClientSize.Height;
            int lw = 470, lh = logo != null ? logo.Height * lw / logo.Width : 0;
            labelsAt = new Point(x, full - 292);
            labels = new Bitmap(lw + 4, 30 + lh, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(labels))
            {
                g.Clear(Color.Transparent); g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using (var caption = new Font("Segoe UI Semibold", 9.5f)) using (var ink = new SolidBrush(Theme.Muted))
                    g.DrawString("BALLISTIC OVERKILL  ·  SERVIDOR PRIVADO", caption, ink, 2, 0);
                if (logo != null) g.DrawImage(logo, 0, 30, lw, lh);
            }
        }
        frame = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        RenderFrame();
        // The glass panel shows the art too; refresh it every 6th tick (art moves <1 px in that time) to keep frames cheap.
        drift.Tick += delegate { RenderFrame(); InvalidateArt(); if (panel.Right > RailW && ++driftTicks % 6 == 0) panel.Invalidate(true); };
        Activated += delegate { if (artLayer != null && !drift.Enabled) { timeBeginPeriod(1); drift.Start(); } };
        Deactivate += delegate { StopDrift(); };
        Resize += delegate { if (WindowState == FormWindowState.Minimized) StopDrift(); };
        FormClosed += delegate { StopDrift(); };
    }

    // Slow horizontal drift (40 s cycle, ±18 px), sub-pixel so it never "steps".
    PointF ArtOffset()
    {
        double t = driftClock.Elapsed.TotalSeconds;
        return new PointF((float)(DriftX * Math.Sin(t * 2 * Math.PI / 40.0)), 0f);
    }
    // Composite art + overlay once per tick into a cached frame; the form and its transparent buttons then only blit it.
    void RenderFrame()
    {
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Theme.Bg);
            if (artLayer != null)
            {
                PointF o = ArtOffset();
                double x = artBaseX + o.X, y = artBaseY + o.Y;
                int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
                int px = (int)Math.Round((x - ix) * PhasesX), py = (int)Math.Round((y - iy) * PhasesY);
                if (px == PhasesX) { px = 0; ix++; }
                if (py == PhasesY) { py = 0; iy++; }
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(artPhases[px, py], ix, iy);
                g.CompositingMode = CompositingMode.SourceOver;
            }
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.DrawImageUnscaled(overlay, 0, 0);
        }
    }
    void InvalidateArt()
    {
        int from = Math.Max(RailW, panel.Right);
        Invalidate(new Rectangle(from, TitleH, ClientSize.Width - from, ClientSize.Height - TitleH), true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImageUnscaled(frame, 0, TitleH); // frame is opaque: plain copy, no blending
        g.CompositingMode = CompositingMode.SourceOver;
        if (labels != null) g.DrawImageUnscaled(labels, labelsAt.X, labelsAt.Y); // logo + caption: window only, never behind the glass
        int x = RailW + 56, h = ClientSize.Height;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var dot = new SolidBrush(liveColor)) g.FillEllipse(dot, x + 2, h - 145, 9, 9);
        TextRenderer.DrawText(g, liveText, statusFont, new Point(x + 18, h - 150), Theme.Text);
    }

    void BuildTitleBar()
    {
        titleBar.SetBounds(0, 0, ClientSize.Width, TitleH); titleBar.BackColor = Theme.Bar; Controls.Add(titleBar);
        titleBar.MouseDown += DragWindow;
        var icon = new PictureBox { Image = Icon != null ? new Icon(Icon, 20, 20).ToBitmap() : null, SizeMode = PictureBoxSizeMode.CenterImage, Bounds = new Rectangle(14, 0, 24, TitleH) };
        icon.MouseDown += DragWindow; titleBar.Controls.Add(icon);
        var title = new Label { Text = "BALLISTIC", Font = new Font("Segoe UI Semibold", 10f), ForeColor = Theme.Text, AutoSize = true, Location = new Point(44, 11) };
        title.MouseDown += DragWindow; titleBar.Controls.Add(title);
        var version = new Label { Text = "launcher " + Updater.Version, Font = new Font("Segoe UI", 8.5f), ForeColor = Theme.Muted, AutoSize = true, Location = new Point(124, 13) };
        version.MouseDown += DragWindow; titleBar.Controls.Add(version);
        updateLabel.Font = new Font("Segoe UI", 8.5f); updateLabel.ForeColor = Theme.Muted; updateLabel.AutoSize = true; updateLabel.Location = new Point(210, 13);
        updateLabel.MouseDown += DragWindow; titleBar.Controls.Add(updateLabel);
        var close = new FlatButton { Glyph = "", Fill = Theme.Bar, HoverFill = Theme.Accent, Radius = 0, Bounds = new Rectangle(ClientSize.Width - 46, 0, 46, TitleH) };
        close.Font = new Font("Segoe UI", 8f); close.Click += delegate { Close(); }; titleBar.Controls.Add(close);
        var min = new FlatButton { Glyph = "", Fill = Theme.Bar, HoverFill = Theme.Field, Radius = 0, Bounds = new Rectangle(ClientSize.Width - 92, 0, 46, TitleH) };
        min.Font = new Font("Segoe UI", 8f); min.Click += delegate { WindowState = FormWindowState.Minimized; }; titleBar.Controls.Add(min);
    }

    void BuildRail()
    {
        rail.SetBounds(0, TitleH, RailW, ClientSize.Height - TitleH); rail.BackColor = Theme.Bar; Controls.Add(rail);
        rail.Paint += delegate(object s, PaintEventArgs e) { using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, RailW - 1, 0, RailW - 1, rail.Height); };
        gear.Glyph = ""; gear.Fill = Theme.Bar; gear.HoverFill = Theme.Field; gear.Font = new Font("Segoe UI", 13f);
        gear.SetBounds(10, 14, 44, 44); gear.Click += delegate { pinned = !pinned; if (!pinned) play.Focus(); };
        rail.Controls.Add(gear);
        var tip = new ToolTip(); tip.SetToolTip(gear, "Configurações");
    }

    Label Caption(string text, int y, bool header = false)
    {
        var l = new Label { Text = text, AutoSize = false, Bounds = new Rectangle(24, y, PanelW - 48, header ? 28 : 20), ForeColor = header ? Theme.Text : Theme.Muted,
            Font = header ? new Font("Segoe UI Semibold", 13f) : new Font("Segoe UI", 9f), BackColor = Color.Transparent };
        panel.Controls.Add(l); return l;
    }
    void Field(TextBox t, int x, int y, int w)
    {
        t.SetBounds(x, y, w, 28); t.BackColor = Theme.Field; t.ForeColor = Theme.Text; t.BorderStyle = BorderStyle.FixedSingle; t.Font = new Font("Segoe UI", 10.5f);
        panel.Controls.Add(t);
    }
    FlatButton PanelButton(FlatButton b, string text, string glyph, int x, int y, int w)
    { b.Text = text; b.Glyph = glyph; b.SetBounds(x, y, w, 36); b.Font = new Font("Segoe UI Semibold", 9.5f); panel.Controls.Add(b); return b; }

    void BuildPanel()
    {
        panel.SetBounds(RailW - PanelW, TitleH, PanelW, ClientSize.Height - TitleH); panel.BackColor = Theme.Panel; Controls.Add(panel);
        panel.Backdrop = PaintPanelBackdrop;
        // No hard border: the panel blends into the art through the fade strip drawn in OnPaintBackground.
        int w = PanelW - 48;
        Caption("Configurações", 20, true);
        Caption("Seu apelido", 58); Field(name, 24, 78, w); name.Text = profile.Name; name.MaxLength = 32;
        Caption("Servidor", 114);
        presetBox.SetBounds(24, 134, w, 28); presetBox.DropDownStyle = ComboBoxStyle.DropDownList; presetBox.FlatStyle = FlatStyle.Flat;
        presetBox.BackColor = Theme.Field; presetBox.ForeColor = Theme.Text; presetBox.Font = new Font("Segoe UI", 10.5f); panel.Controls.Add(presetBox);
        Field(host, 24, 168, w - 92); host.Text = profile.Host;
        port.SetBounds(24 + w - 84, 168, 84, 28); port.Minimum = 1; port.Maximum = 65535; port.Value = profile.QueryPort;
        port.BackColor = Theme.Field; port.ForeColor = Theme.Text; port.BorderStyle = BorderStyle.FixedSingle; port.Font = new Font("Segoe UI", 10.5f); panel.Controls.Add(port);
        foreach (var p in presets) presetBox.Items.Add(p);
        presetBox.Items.Add(CustomPreset);
        SyncPresetFromFields();
        presetBox.SelectedIndexChanged += delegate { ApplyPreset(); };
        host.TextChanged += delegate { SyncPresetFromFields(); };
        port.ValueChanged += delegate { SyncPresetFromFields(); };
        Caption("Outros jogadores (opcional, separados por vírgula)", 204); Field(peers, 24, 224, w); peers.Text = profile.Peers;
        PanelButton(new FlatButton(), "Salvar", "", 24, 262, (w - 8) / 2).Click += delegate { Guard(delegate { SaveProfile(); SetPanelStatus("Configuração salva.", Theme.Ok); RefreshLiveStatus(); }); };
        PanelButton(query, "Testar servidor", "", 24 + (w + 8) / 2, 262, (w - 8) / 2).Click += delegate { RefreshLiveStatus(true); };

        Caption("Progresso", 314, true);
        Caption("Libera armas, habilidades e loadouts de todas as classes.", 342);
        // Solid colors (same look as the old 27%/47% red over the panel) so the glass never shows through buttons.
        unlock.Fill = Color.FromArgb(74, 24, 34); unlock.HoverFill = Color.FromArgb(115, 27, 40);
        PanelButton(unlock, "Desbloquear tudo", "", 24, 364, w).Click += delegate { Guard(UnlockAll); };

        int y = 416;
        if (serverExe != null)
        {
            Caption("Servidor (anfitrião)", y, true);
            Caption("Roda o servidor dedicado neste PC, em segundo plano.", y + 30);
            PanelButton(server, "Iniciar servidor", "", 24, y + 54, w).Click += delegate { Guard(ToggleServer); };
            serverPoll.Tick += delegate { UpdateServerButton(); }; serverPoll.Start(); UpdateServerButton();
            y += 112;
        }
        panelStatus.SetBounds(24, y + 4, w, 60); panelStatus.ForeColor = Theme.Muted; panelStatus.BackColor = Color.Transparent; panelStatus.Font = new Font("Segoe UI", 9f);
        panel.Controls.Add(panelStatus);
    }

    // ---------- server presets ----------
    readonly ComboBox presetBox = new ComboBox();
    List<ServerPreset> presets;
    static readonly string CustomPreset = "Personalizado";
    bool syncingPreset;
    void ApplyPreset()
    {
        if (syncingPreset) return;
        var p = presetBox.SelectedItem as ServerPreset;
        if (p == null) return; // "Personalizado": keep whatever is typed
        if (!p.Configured)
        {
            SetPanelStatus("O endereço de \"" + p.Name + "\" ainda não foi definido em servidores.json.", Theme.Accent);
            BeginInvoke((Action)SyncPresetFromFields); // snap the selection back to what the fields actually contain
            return;
        }
        syncingPreset = true;
        host.Text = p.Address; port.Value = p.QueryPort;
        syncingPreset = false;
        SetPanelStatus("Servidor: " + p.Name + ". Clique em Salvar ou Jogar.", Theme.Muted);
    }
    // Select the preset that matches the typed address/port, or "Personalizado".
    void SyncPresetFromFields()
    {
        if (syncingPreset) return;
        object match = CustomPreset;
        foreach (var p in presets)
            if (p.Configured && String.Equals(p.Address.Trim(), host.Text.Trim(), StringComparison.OrdinalIgnoreCase) && p.QueryPort == (int)port.Value) { match = p; break; }
        syncingPreset = true; presetBox.SelectedItem = match; syncingPreset = false;
    }

    // Glass effect: copy the window art that sits behind the panel (frame is anchored to the window, so offset by the
    // panel position), then lay a panel-colored diagonal fade over it: nearly opaque top-left -> see-through bottom-right.
    void PaintPanelBackdrop(Graphics g)
    {
        g.CompositingMode = CompositingMode.SourceCopy;
        if (frame != null) g.DrawImageUnscaled(frame, -panel.Left, 0); else g.Clear(Theme.Panel);
        g.CompositingMode = CompositingMode.SourceOver;
        if (glass == null || glass.Height != panel.Height)
        {
            // Pre-rendered once: gradient fills are the expensive part and every transparent label repaints this.
            glass = new Bitmap(panel.Width, panel.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var gg = Graphics.FromImage(glass))
            using (var b = new LinearGradientBrush(new Rectangle(-4, -4, glass.Width + 8, glass.Height + 8), Theme.Panel, Theme.Panel, 45f, true))
            {
                var blend = new ColorBlend(4);
                blend.Colors = new[] { Color.FromArgb(GlassAlphaTopLeft, Theme.Panel), Color.FromArgb(225, Theme.Panel), Color.FromArgb(160, Theme.Panel), Color.FromArgb(GlassAlphaBottomRight, Theme.Panel) };
                blend.Positions = new[] { 0f, 0.45f, 0.75f, 1f };
                b.InterpolationColors = blend;
                gg.Clear(Color.Transparent);
                gg.FillRectangle(b, 0, 0, glass.Width, glass.Height);
            }
        }
        g.DrawImageUnscaled(glass, 0, 0);
    }

    void BuildMain()
    {
        int x = RailW + 56, h = ClientSize.Height;
        play.Text = "Jogar"; play.Glyph = ""; play.Fill = Color.FromArgb(245, 246, 248); play.HoverFill = Color.White; play.Ink = Color.FromArgb(14, 16, 20);
        play.Font = new Font("Segoe UI Semibold", 13f); play.SetBounds(x, h - 116, 230, 58); play.Click += delegate { Guard(Play); };
        settings.Text = "Configurações"; settings.Glyph = ""; settings.Fill = Color.FromArgb(200, 28, 33, 41); settings.HoverFill = Color.FromArgb(230, 40, 47, 58);
        settings.SetBounds(x + 244, h - 116, 180, 58); settings.Click += delegate { pinned = !pinned; if (!pinned) play.Focus(); };
        Controls.Add(play); Controls.Add(settings);
    }

    // Slide the settings panel in while the mouse is over the rail/panel, it is pinned, or a field has focus.
    void UpdatePanel()
    {
        Point m = PointToClient(Cursor.Position);
        bool overRail = new Rectangle(0, TitleH, RailW, ClientSize.Height).Contains(m);
        bool overPanel = panel.Right > RailW && panel.Bounds.Contains(m);
        bool open = pinned || overRail || overPanel || panel.ContainsFocus;
        int target = open ? RailW : RailW - PanelW;
        if (target == slideTo) return;
        // Time-based ease-out slide (~180 ms) instead of per-tick fractions, which crawled at the end.
        slideFrom = panel.Left; slideTo = target; slideStart = DateTime.UtcNow; slide.Start();
        gear.Fill = open ? Theme.Field : Theme.Bar; gear.Invalidate();
    }
    void SlideStep()
    {
        double t = Math.Min(1.0, (DateTime.UtcNow - slideStart).TotalMilliseconds / SlideMs);
        double eased = 1 - Math.Pow(1 - t, 3);
        int oldRight = panel.Right;
        panel.Left = slideFrom + (int)Math.Round((slideTo - slideFrom) * eased);
        // The fade strip lives on the form, right of the panel: repaint the band it moved through.
        int from = Math.Min(oldRight, panel.Right);
        Invalidate(new Rectangle(from, TitleH, Math.Abs(panel.Right - oldRight) + 2, ClientSize.Height - TitleH), true);
        panel.Invalidate(true); // its see-through backdrop is window-anchored, so it must repaint while sliding
        panel.Update(); Update();
        if (t >= 1) slide.Stop();
    }

    void Guard(Action action)
    {
        try { action(); }
        catch (Exception e) { SetPanelStatus(e.Message, Theme.Accent); pinned = true; }
    }
    void SetPanelStatus(string text, Color color) { panelStatus.Text = text; panelStatus.ForeColor = color; }

    bool GameRunning()
    {
        string exe = Path.GetFullPath(Path.Combine(root, "BallisticOverkill.exe"));
        foreach (var p in Process.GetProcessesByName("BallisticOverkill"))
            using (p) { try { if (String.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true; } catch { } }
        return false;
    }
    void SaveProfile()
    {
        if (GameRunning()) throw new InvalidOperationException("Feche o jogo antes de alterar a configuração.");
        profile.Name = name.Text.Trim(); profile.Host = host.Text.Trim(); profile.Peers = peers.Text.Trim(); profile.QueryPort = (int)port.Value;
        profile.Save(root);
    }
    void Play()
    {
        string exe = Path.Combine(root, "BallisticOverkill.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Extraia o pacote inteiro antes de jogar. BallisticOverkill.exe não foi encontrado.");
        SaveProfile();
        // Resolution and fullscreen come from the game's own video menu (BallisticConfiguration.settings).
        Process.Start(new ProcessStartInfo(exe, "-logfile cliente-lan.log") { WorkingDirectory = root, UseShellExecute = false });
        liveText = "Jogo iniciado · Community Servers → Browse Servers → Ballistic"; liveColor = Theme.Ok; Invalidate(new Rectangle(0, ClientSize.Height - 160, 900, 30));
    }
    void UnlockAll()
    {
        if (GameRunning()) throw new InvalidOperationException("Feche o jogo antes de desbloquear.");
        var answer = MessageBox.Show(this, "Isso coloca todas as classes no nível máximo e libera todas as armas, habilidades e loadouts.\n\nUm backup do seu progresso atual será salvo em PerfilLAN. Continuar?",
            "Desbloquear tudo", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        string backup = Progress.UnlockAll(root);
        SetPanelStatus("Pronto! Tudo será liberado na próxima vez que o jogo abrir." + (backup != null ? "\nBackup: PerfilLAN\\" + Path.GetFileName(backup) : ""), Theme.Ok);
    }

    // ---------- host server ----------
    Process[] ServerProcesses()
    {
        var found = new List<Process>();
        foreach (var p in Process.GetProcessesByName("BODS"))
        {
            bool mine = false;
            try { mine = String.Equals(p.MainModule.FileName, serverExe, StringComparison.OrdinalIgnoreCase); } catch { }
            if (mine) found.Add(p); else p.Dispose();
        }
        return found.ToArray();
    }
    void UpdateServerButton()
    {
        var running = ServerProcesses();
        bool on = running.Length > 0;
        string text = on ? "Parar servidor" : "Iniciar servidor";
        if (server.Text != text) { server.Text = text; server.Glyph = on ? "" : ""; server.Fill = on ? Color.FromArgb(46, 125, 80) : Theme.Field; server.HoverFill = on ? Color.FromArgb(56, 145, 95) : Color.FromArgb(40, 47, 58); server.Invalidate(); }
        foreach (var p in running) p.Dispose();
    }
    void ToggleServer()
    {
        var running = ServerProcesses();
        if (running.Length > 0)
        {
            foreach (var p in running) using (p) { p.Kill(); p.WaitForExit(5000); }
            SetPanelStatus("Servidor parado.", Theme.Muted);
        }
        else
        {
            Process.Start(new ProcessStartInfo(serverExe, "-batchmode -nographics -logfile server.log") { WorkingDirectory = Path.GetDirectoryName(serverExe), UseShellExecute = false, CreateNoWindow = true });
            SetPanelStatus("Servidor iniciando… o status aparece em alguns segundos.", Theme.Ok);
        }
        UpdateServerButton();
    }

    // ---------- live server status ----------
    async void RefreshLiveStatus(bool fromButton = false)
    {
        if (querying) return;
        querying = true; query.Enabled = false;
        int ignored; string address;
        try { address = PlayerProfile.SplitAddress(host.Text.Trim(), PlayerProfile.ServerNetPort, out ignored); }
        catch (Exception e) { liveText = e.Message; liveColor = Theme.Accent; Invalidate(); querying = false; query.Enabled = true; return; }
        int queryPort = (int)port.Value;
        try
        {
            string result = await Task.Run(() => ReadServer(address, queryPort));
            liveText = result + "  ·  " + address; liveColor = Theme.Ok;
            if (fromButton) SetPanelStatus("Servidor respondeu: " + result, Theme.Ok);
        }
        catch
        {
            liveText = "Servidor offline ou inacessível  ·  " + address + ":" + queryPort; liveColor = Theme.Muted;
            if (fromButton) SetPanelStatus("Sem resposta de " + address + ":" + queryPort + ".", Theme.Accent);
        }
        finally { querying = false; query.Enabled = true; Invalidate(new Rectangle(0, ClientSize.Height - 160, ClientSize.Width, 30)); }
    }
    public static string ReadServer(string host, int port)
    {
        using (var udp = new UdpClient(AddressFamily.InterNetwork))
        {
            udp.Client.ReceiveTimeout = 2500; udp.Connect(host, port);
            byte[] packet = new byte[] { 255, 255, 255, 255, 84 }.Concat(Encoding.ASCII.GetBytes("Source Engine Query\0")).ToArray();
            udp.Send(packet, packet.Length); var ep = new IPEndPoint(IPAddress.Any, 0); byte[] response = udp.Receive(ref ep);
            if (response.Length >= 9 && response[4] == 65)
            { packet = packet.Concat(response.Skip(5).Take(4)).ToArray(); udp.Send(packet, packet.Length); response = udp.Receive(ref ep); }
            if (response.Length < 7 || response[4] != 73) throw new InvalidDataException("Resposta de consulta não reconhecida.");
            int offset = 6; string serverName = "";
            for (int i = 0; i < 4; i++)
            { int end = offset; while (end < response.Length && response[end] != 0) end++; if (end == response.Length) throw new InvalidDataException("Resposta incompleta."); if (i == 0) serverName = Encoding.UTF8.GetString(response, offset, end - offset); offset = end + 1; }
            if (offset + 3 >= response.Length) throw new InvalidDataException("Resposta incompleta.");
            return serverName + "  ·  " + response[offset + 2] + "/" + response[offset + 3] + " jogadores";
        }
    }
    // ---------- auto-update ----------
    readonly Label updateLabel = new Label();
    async void CheckForUpdates()
    {
        updateLabel.Text = "· verificando atualizações…";
        try
        {
            bool gameOpen = GameRunning();
            var result = await Task.Run(() => Updater.Run(root, gameOpen, delegate(string s) { BeginInvoke((Action)delegate { updateLabel.Text = "· " + s; }); }));
            if (result.RestartNeeded)
            {
                updateLabel.Text = "· versão " + result.RemoteVersion + " instalada, reiniciando…"; updateLabel.ForeColor = Theme.Ok;
                await Task.Delay(1200);
                Process.Start(new ProcessStartInfo(Application.ExecutablePath) { WorkingDirectory = root, UseShellExecute = false });
                Close();
                return;
            }
            if (result.UpdatedFiles.Contains("servidores.json")) ReloadPresets();
            updateLabel.Text = result.UpdatedFiles.Count > 0 ? "· atualizado (" + result.UpdatedFiles.Count + " arquivo(s))" : (result.Deferred ? "· atualização pendente: feche o jogo" : "· atualizado");
            updateLabel.ForeColor = result.UpdatedFiles.Count > 0 ? Theme.Ok : Theme.Muted;
        }
        catch (Exception e)
        {
            // Offline or GitHub unreachable must never block playing.
            updateLabel.Text = "· sem atualização (" + Updater.Short(e) + ")"; updateLabel.ForeColor = Theme.Muted;
        }
    }
    void ReloadPresets()
    {
        presets = ServerList.Load(root);
        syncingPreset = true; presetBox.Items.Clear(); foreach (var p in presets) presetBox.Items.Add(p); presetBox.Items.Add(CustomPreset); syncingPreset = false;
        SyncPresetFromFields();
    }

    [STAThread]
    public static void Main()
    {
        Updater.CleanupAfterSelfUpdate();
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        try { Application.Run(new LauncherForm()); }
        catch (Exception e) { MessageBox.Show(e.Message, "Ballistic", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}

// Double-buffered container so the sliding settings panel repaints without tearing.
public class BufferedPanel : Panel
{
    public BufferedPanel() { DoubleBuffered = true; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
}

// Server presets shown in the launcher ("Local (este PC)", "VPS", ...). Read from servidores.json next to the launcher;
// master copy lives in C:\Ballistic\config\servidores.json and is synced by scripts\Aplicar-Perfil.ps1.
[System.Runtime.Serialization.DataContract]
public sealed class ServerPreset
{
    [System.Runtime.Serialization.DataMember(Name = "nome")] public string Name;
    [System.Runtime.Serialization.DataMember(Name = "endereco")] public string Address;
    [System.Runtime.Serialization.DataMember(Name = "porta")] public int QueryPort;
    public bool Configured { get { return !String.IsNullOrWhiteSpace(Address); } }
    public override string ToString() { return Configured ? Name : Name + " (não configurado)"; }
}
[System.Runtime.Serialization.DataContract]
public sealed class ServerList
{
    [System.Runtime.Serialization.DataMember(Name = "servidores")] public List<ServerPreset> Servers = new List<ServerPreset>();
    public static List<ServerPreset> Load(string root)
    {
        string path = Path.Combine(root, "servidores.json");
        var fallback = new List<ServerPreset> { new ServerPreset { Name = "Local (este PC)", Address = "127.0.0.1", QueryPort = 28016 } };
        if (!File.Exists(path)) return fallback;
        try
        {
            using (var s = File.OpenRead(path))
            {
                var list = (ServerList)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(ServerList)).ReadObject(s);
                var valid = list.Servers.Where(p => p != null && !String.IsNullOrWhiteSpace(p.Name)).ToList();
                foreach (var p in valid) if (p.QueryPort <= 0 || p.QueryPort > 65535) p.QueryPort = 28016;
                return valid.Count > 0 ? valid : fallback;
            }
        }
        catch { return fallback; }
    }
}

// Auto-update from GitHub Releases (public repo; contains no game files).
// Each release has manifest.json (+ manifest.sig = RSA-SHA256 by the private key kept only on the host PC).
// The launcher downloads only files whose SHA-256 differs, verifies each hash, then swaps them in.
// Only a fixed set of paths can ever be written (never player identity/config files).
[System.Runtime.Serialization.DataContract]
public sealed class UpdateFile
{
    [System.Runtime.Serialization.DataMember(Name = "caminho")] public string Path;
    [System.Runtime.Serialization.DataMember(Name = "sha256")] public string Sha256;
    [System.Runtime.Serialization.DataMember(Name = "tamanho")] public long Size;
    [System.Runtime.Serialization.DataMember(Name = "url")] public string Url;
}
[System.Runtime.Serialization.DataContract]
public sealed class UpdateManifest
{
    [System.Runtime.Serialization.DataMember(Name = "versao")] public string Version;
    [System.Runtime.Serialization.DataMember(Name = "arquivos")] public List<UpdateFile> Files = new List<UpdateFile>();
}
public sealed class UpdateResult
{
    public string RemoteVersion;
    public bool RestartNeeded, Deferred;
    public List<string> UpdatedFiles = new List<string>();
}
public static class Updater
{
    public const string Version = "0.5";
    public const string Repo = "MiguelMussalam/ballistic-launcher";
    const string PublicKeyXml = "<RSAKeyValue><Modulus>yZTNj2m7COE7sXRyrZkt/0KIapxj9GHVMOoTS2X80oXh0NC3uQLIofCgOJhNY+6gVyczjtO1k76Wrn4B45uYEkHIeZLDtUcKUnaZweaI2f+EllCuAxE5uAr3LFQsjVJGzlXJwk9FO4pjzl1or1XSPy/n3N4Z1YQLfehNJ8UYapXMJZ1vlz/f3W0RfAhDtiUAWHZ5x5WKxTAFTGioi8790fIEwRsR6+fesapwuL0eiH21UHJ7KqGAd+jIZzK8mg/egnJ42mDzemIIxhIEvNvXVNVlhWLdxlDzOZueHez29PQrOshUn65eZ2r99QJi4zGXjyn8NQ65W3o6JjN9AuOAZQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
    const string SettingsDir = "BallisticOverkill_Data/Plugins/steam_settings/";
    static readonly string[] AllowedSettings = { "stats.json", "items.json", "default_items.json", "configs.app.ini", "steam_interfaces.txt" };

    static bool Allowed(string path)
    {
        if (String.IsNullOrEmpty(path) || path.Contains("..") || path.Contains("\\") || path.Contains(":") || path.StartsWith("/")) return false;
        if (path == "Ballistic.exe" || path == "servidores.json") return true;
        return path.StartsWith(SettingsDir) && AllowedSettings.Contains(path.Substring(SettingsDir.Length));
    }
    static byte[] Get(string url)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "Ballistic-Launcher/" + Version; req.Timeout = 15000; req.ReadWriteTimeout = 30000; req.AllowAutoRedirect = true;
        using (var resp = req.GetResponse()) using (var s = resp.GetResponseStream()) using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
    }
    static string Sha256(byte[] data) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
    static string Sha256File(string path) { if (!File.Exists(path)) return ""; using (var s = File.OpenRead(path)) using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
    public static bool VerifySignature(byte[] data, byte[] signature)
    {
        using (var rsa = new RSACryptoServiceProvider(new CspParameters(24))) // PROV_RSA_AES: supports SHA-256
        { rsa.PersistKeyInCsp = false; rsa.FromXmlString(PublicKeyXml); return rsa.VerifyData(data, "SHA256", signature); }
    }
    public static string Short(Exception e) { var m = e.GetBaseException().Message; return m.Length > 60 ? m.Substring(0, 60) + "…" : m; }

    public static UpdateResult Run(string root, bool gameRunning, Action<string> progress)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2 (GitHub)
        string baseUrl = "https://github.com/" + Repo + "/releases/latest/download/";
        byte[] manifestBytes = Get(baseUrl + "manifest.json");
        byte[] signature = Convert.FromBase64String(Encoding.ASCII.GetString(Get(baseUrl + "manifest.sig")).Trim());
        if (!VerifySignature(manifestBytes, signature)) throw new InvalidDataException("assinatura inválida");
        UpdateManifest manifest;
        using (var ms = new MemoryStream(manifestBytes))
            manifest = (UpdateManifest)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(UpdateManifest)).ReadObject(ms);

        var result = new UpdateResult { RemoteVersion = manifest.Version };
        var pending = manifest.Files.Where(f => Allowed(f.Path) && !String.Equals(Sha256File(Path.Combine(root, f.Path)), f.Sha256, StringComparison.OrdinalIgnoreCase)).ToList();
        if (pending.Count == 0) return result;
        if (gameRunning && pending.Any(f => f.Path != "Ballistic.exe" && f.Path != "servidores.json")) { result.Deferred = true; pending = pending.Where(f => f.Path == "Ballistic.exe" || f.Path == "servidores.json").ToList(); }

        string staging = Path.Combine(root, "launcher", "update");
        Directory.CreateDirectory(staging);
        var downloaded = new List<KeyValuePair<UpdateFile, string>>();
        for (int i = 0; i < pending.Count; i++)
        {
            var f = pending[i];
            progress("baixando " + (i + 1) + "/" + pending.Count + "…");
            if (!f.Url.StartsWith("https://github.com/" + Repo + "/")) throw new InvalidDataException("URL fora do repositório");
            byte[] data = Get(f.Url);
            if (!String.Equals(Sha256(data), f.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("hash não confere: " + f.Path);
            string tmp = Path.Combine(staging, Path.GetFileName(f.Path) + ".new");
            File.WriteAllBytes(tmp, data);
            downloaded.Add(new KeyValuePair<UpdateFile, string>(f, tmp));
        }
        foreach (var d in downloaded)
        {
            string target = Path.Combine(root, d.Key.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (d.Key.Path == "Ballistic.exe")
            {
                // A running exe can be renamed but not overwritten: move it aside, drop the new one in, restart.
                string self = Application.ExecutablePath, old = self + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(self, old);
                File.Move(d.Value, self);
                result.RestartNeeded = true;
            }
            else
            {
                File.Copy(d.Value, target, true); File.Delete(d.Value);
            }
            result.UpdatedFiles.Add(d.Key.Path);
        }
        return result;
    }
    public static void CleanupAfterSelfUpdate()
    {
        try { string old = Application.ExecutablePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
    }
}

// Settings panel with a see-through backdrop: the form supplies the painter (window art + diagonal panel-color fade).
// Child labels use transparent backgrounds so they inherit it.
public class GlassPanel : BufferedPanel
{
    public Action<Graphics> Backdrop;
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Backdrop != null) Backdrop(e.Graphics); else base.OnPaintBackground(e);
    }
}
