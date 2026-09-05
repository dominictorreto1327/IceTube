using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IceTube.Controls
{
    // A persistent HWND for mpv's --wid child. Do not double-buffer a native
    // video host: painting over the external child can cause flicker/black video.
    public sealed class VideoSurface : Panel
    {
        public VideoSurface()
        {
            BackColor = Color.Black;
            TabStop = false;
            BorderStyle = BorderStyle.None;
        }

        public static Rectangle FitBounds(Size available)
        {
            // Integral 16x9 units keep the viewport ratio exact at every size.
            int units = Math.Max(0, Math.Min(available.Width / 16, available.Height / 9));
            Size size = new Size(units * 16, units * 9);
            return new Rectangle((available.Width - size.Width) / 2,
                (available.Height - size.Height) / 2, size.Width, size.Height);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.Style |= 0x02000000 | 0x04000000; // CLIPCHILDREN | CLIPSIBLINGS
                return parameters;
            }
        }

        protected override void OnResize(EventArgs eventArgs)
        {
            base.OnResize(eventArgs);
            if (!IsHandleCreated) return;
            // Keep mpv's native child at the full viewport size after resizing.
            for (IntPtr child = GetWindow(Handle, 5); child != IntPtr.Zero; child = GetWindow(child, 2))
                MoveWindow(child, 0, 0, ClientSize.Width, ClientSize.Height, true);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        private static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    }
}
