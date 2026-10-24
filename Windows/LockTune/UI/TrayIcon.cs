using System;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace LockTune.UI;

/// The taskbar-corner icon: click = show the LockTune screen, right-click = menu.
public sealed class TrayIcon : IDisposable
{
    private readonly AppCoordinator app;
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ToolStripMenuItem lockItem = new();
    private readonly Forms.ToolStripMenuItem turntableItem = new("Desktop turntable");
    private bool balloonInstalls;

    public TrayIcon(AppCoordinator app)
    {
        this.app = app;
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/LockTune.ico"))?.Stream;
        icon = new Forms.NotifyIcon
        {
            Icon = stream != null ? new Icon(stream, Forms.SystemInformation.SmallIconSize) : SystemIcons.Application,
            Text = "LockTune",
            Visible = true,
        };

        var menu = new Forms.ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, ShowCheckMargin = true, Font = new Font("Segoe UI", 9.5f) };
        menu.Items.Add(new Forms.ToolStripMenuItem("Show LockTune", null, (_, _) => Later(app.ShowPreview)) { Font = new Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold) });
        lockItem.Click += (_, _) => Later(app.LockNow);
        menu.Items.Add(lockItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        turntableItem.Click += (_, _) => app.Settings.TurntableEnabled = !app.Settings.TurntableEnabled;
        menu.Items.Add(turntableItem);
        menu.Items.Add(new Forms.ToolStripMenuItem("Settings…", null, (_, _) => app.OpenSettings()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Check for updates", null, (_, _) => app.Updater.CheckNow(true)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Quit LockTune", null, (_, _) => app.Quit()));
        icon.ContextMenuStrip = menu;

        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Later(app.ShowPreview);
        };
        icon.BalloonTipClicked += (_, _) =>
        {
            if (balloonInstalls) app.InstallUpdate();
            else app.OpenSettings();
        };
        Refresh();
    }

    public void Refresh()
    {
        lockItem.Text = app.Settings.HotkeyEnabled ? $"Lock now\t{app.Settings.Hotkey}" : "Lock now";
        turntableItem.Checked = app.Settings.TurntableEnabled;
    }

    public void Notify(string title, string text, bool install = false)
    {
        balloonInstalls = install;
        icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
    }

    /// Menu items fire while the menu is still closing; wait a beat.
    private static void Later(Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }

    private sealed class DarkRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = true; }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Color.FromArgb(240, 240, 245);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using var pen = new Pen(Color.FromArgb(143, 180, 255), 2);
            e.Graphics.DrawLines(pen, new[] { new System.Drawing.Point(r.Left + 3, r.Top + r.Height / 2), new System.Drawing.Point(r.Left + r.Width / 2 - 1, r.Bottom - 4), new System.Drawing.Point(r.Right - 3, r.Top + 4) });
        }
    }

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        private static readonly Color Back = Color.FromArgb(32, 32, 40);
        private static readonly Color Hover = Color.FromArgb(58, 58, 72);
        public override Color ToolStripDropDownBackground => Back;
        public override Color MenuBorder => Color.FromArgb(70, 70, 82);
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color SeparatorDark => Color.FromArgb(60, 60, 70);
        public override Color SeparatorLight => Back;
        public override Color CheckBackground => Back;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
