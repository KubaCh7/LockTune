using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace LockTune.UI;

/// The ♪-with-a-house icon in the taskbar corner and its menu (the Mac's
/// menu-bar item, StatusItemController). A click opens the menu, like on the Mac.
public sealed class TrayIcon : IDisposable
{
    private readonly AppCoordinator app;
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu;

    public TrayIcon(AppCoordinator app)
    {
        this.app = app;
        icon = new Forms.NotifyIcon { Icon = MakeIcon(), Text = "LockTune", Visible = true };
        menu = new Forms.ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 9.5f) };
        if (!LightTaskbar()) menu.Renderer = new DarkRenderer();
        menu.Opening += (_, _) => Rebuild();
        icon.ContextMenuStrip = menu;
        icon.MouseClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            // NotifyIcon only opens its menu on a right click; open it on a left click too.
            typeof(Forms.NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(icon, null);
        };
        icon.BalloonTipClicked += (_, _) => app.OpenSettings();
        SystemEvents.UserPreferenceChanged += OnThemeChanged;
        Rebuild();
    }

    /// Rebuilt each time it opens, like the Mac menu.
    private void Rebuild()
    {
        menu.Items.Clear();
        var media = app.Media;
        var nowPlaying = media.Track is { } t ? $"{(media.IsPlaying ? "▶" : "❚❚")}  {t.Title} — {t.Artist}" : "Nothing playing";
        menu.Items.Add(new Forms.ToolStripMenuItem(nowPlaying) { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        var settings = app.Settings;
        menu.Items.Add(new Forms.ToolStripMenuItem("Lock Screen Now", null, (_, _) => Later(app.LockNow))
        {
            ShortcutKeyDisplayString = settings.HotkeyEnabled ? settings.Hotkey.ToString() : null,
        });
        menu.Items.Add(new Forms.ToolStripMenuItem("Preview Widget", null, (_, _) => Later(app.TogglePreview)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Status & Settings…", null, (_, _) => app.OpenSettings()));
        if (app.Updater.UpdateAvailable)
            menu.Items.Add(new Forms.ToolStripMenuItem($"Install Update (build {app.Updater.AvailableBuild})…", null, (_, _) => app.InstallUpdate()));
        else
            menu.Items.Add(new Forms.ToolStripMenuItem("Check for Updates…", null, (_, _) => app.Updater.CheckNow(true)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Quit LockTune", null, (_, _) => app.Quit()));
    }

    public void Notify(string title, string text) => icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);

    /// Menu items fire while the menu is still closing; wait a beat.
    private static void Later(Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    private void OnThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var old = icon.Icon;
            icon.Icon = MakeIcon();
            old?.Dispose();
            menu.Renderer = LightTaskbar() ? new Forms.ToolStripProfessionalRenderer() : new DarkRenderer();
        });
    }

    /// Like a template image on the Mac: white on a dark taskbar, black on a light one.
    private static bool LightTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    private static Icon MakeIcon()
    {
        const int px = 32;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            Symbols.Draw(dc, "music.note.house", LightTaskbar() ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White,
                         new Rect(0, 0, px, px), 1.15);
        var bitmap = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[px * px * 4];
        bitmap.CopyPixels(pixels, px * 4, 0);
        using var bmp = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, px, px), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        bmp.UnlockBits(data);
        var handle = bmp.GetHicon();
        var result = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return result;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        icon.Visible = false;
        icon.Dispose();
    }

    private sealed class DarkRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = true; }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? System.Drawing.Color.FromArgb(240, 240, 245) : System.Drawing.Color.FromArgb(140, 140, 150);
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        private static readonly System.Drawing.Color Back = System.Drawing.Color.FromArgb(42, 42, 48);
        private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(10, 132, 255);
        public override System.Drawing.Color ToolStripDropDownBackground => Back;
        public override System.Drawing.Color MenuBorder => System.Drawing.Color.FromArgb(70, 70, 80);
        public override System.Drawing.Color MenuItemBorder => Hover;
        public override System.Drawing.Color MenuItemSelected => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override System.Drawing.Color ImageMarginGradientBegin => Back;
        public override System.Drawing.Color ImageMarginGradientMiddle => Back;
        public override System.Drawing.Color ImageMarginGradientEnd => Back;
        public override System.Drawing.Color SeparatorDark => System.Drawing.Color.FromArgb(64, 64, 72);
        public override System.Drawing.Color SeparatorLight => Back;
    }
}
