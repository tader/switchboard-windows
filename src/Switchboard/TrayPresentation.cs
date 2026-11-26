using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Switchboard;

internal sealed class TrayPresentation : IDisposable
{
    readonly Forms.NotifyIcon icon;
    readonly Icon normal;
    readonly Icon attention;
    readonly Forms.ToolStripMenuItem signIn;
    internal Forms.ContextMenuStrip Menu { get; } = new();

    internal TrayPresentation(Icon source, Action signInAction)
    {
        normal = source;
        attention = MakeBadge(source);
        signIn = new Forms.ToolStripMenuItem();
        signIn.Click += (_, _) => signInAction();
        Menu.Items.Add(signIn);
        icon = new Forms.NotifyIcon { Icon = normal, ContextMenuStrip = Menu };
        Update("Not running", 0);
        icon.Visible = true;
    }

    internal void OnPrimaryClick(Action action) =>
        icon.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) action(); };

    internal void Update(string status, int count)
    {
        signIn.Available = count > 0;
        signIn.Text = $"Sign in to connections ({count})";
        icon.Text = count switch {
            1 => "Switchboard: 1 connection needs sign-in",
            > 1 => $"Switchboard: {count} connections need sign-in",
            _ => $"Switchboard: {status}"
        };
        icon.Icon = count > 0 ? attention : normal;
    }

    internal Icon CurrentIcon => icon.Icon!;
    internal string Tooltip => icon.Text;

    static Icon MakeBadge(Icon source)
    {
        var size = Forms.SystemInformation.SmallIconSize;
        using var bitmap = new Bitmap(size.Width, size.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawIcon(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        float diameter = Math.Max(9, bitmap.Width * .60f);
        float x = bitmap.Width - diameter;
        float y = bitmap.Height - diameter;
        // Fixed black and white keep the badge legible against both taskbar themes.
        graphics.FillEllipse(Brushes.White, x, y, diameter, diameter);
        graphics.FillEllipse(Brushes.Black, x + 1, y + 1, diameter - 2, diameter - 2);
        using var stroke = new Pen(Color.White, Math.Max(1, diameter / 7));
        float center = x + diameter / 2;
        graphics.DrawLine(stroke, center, y + diameter * .25f, center, y + diameter * .57f);
        graphics.FillEllipse(Brushes.White, center - stroke.Width / 2, y + diameter * .68f, stroke.Width, stroke.Width);
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr iconHandle);

    public void Dispose()
    {
        icon.Dispose();
        Menu.Dispose();
        attention.Dispose();
        normal.Dispose();
    }
}
