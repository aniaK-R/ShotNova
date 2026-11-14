using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ShotNova;

internal static class ScreenGrabber
{
    /// <summary>Captura toda a área virtual (todos os monitores) em pixels físicos, a 96 dpi lógicos.</summary>
    public static BitmapSource GrabVirtualScreen(out Rectangle virtualScreen)
    {
        virtualScreen = Forms.SystemInformation.VirtualScreen;
        using var bmp = new Bitmap(virtualScreen.Width, virtualScreen.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(virtualScreen.X, virtualScreen.Y, 0, 0, virtualScreen.Size, CopyPixelOperation.SourceCopy);
        }

        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgr32, null,
                data.Scan0, data.Stride * data.Height, data.Stride);
            src.Freeze();
            return src;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
