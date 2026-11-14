using System;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace ShotNova;

/// <summary>
/// Moldura vermelha ao redor da área gravada e uma barra com cronômetro e botão Parar.
/// As duas janelas são excluídas da captura, então não aparecem no vídeo.
/// </summary>
public sealed class RecorderUi : IDisposable
{
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    private readonly Func<Rectangle> _region;
    private readonly Window _pill;
    private readonly Window _frame;
    private readonly TextBlock _time = new() { Foreground = System.Windows.Media.Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 42 };
    private readonly Shapes.Ellipse _dot = new() { Width = 10, Height = 10, Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x39, 0x35)), Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DateTime _t0 = DateTime.Now;
    private readonly IntPtr _pillHwnd;
    private readonly IntPtr _frameHwnd;

    public event Action? StopRequested;

    public RecorderUi(Func<Rectangle> region)
    {
        _region = region;

        var stop = new Button
        {
            Content = "■  Parar",
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(10, 0, 0, 0),
            Style = (Style)System.Windows.Application.Current.Resources["Primary"]
        };
        stop.Click += (_, _) => StopRequested?.Invoke();

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dot);
        row.Children.Add(_time);
        row.Children.Add(stop);

        _pill = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"),
            Title = "ShotNova - gravando",
            Content = new Border
            {
                Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["PanelBrush"],
                BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["LineBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 6, 6),
                Child = row
            }
        };

        _frame = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Width = 100,
            Height = 100,
            Title = "ShotNova - moldura",
            Content = new Border
            {
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x39, 0x35)),
                BorderThickness = new Thickness(3),
                IsHitTestVisible = false
            }
        };

        _pillHwnd = new WindowInteropHelper(_pill).EnsureHandle();
        _frameHwnd = new WindowInteropHelper(_frame).EnsureHandle();
        Win.MakeOverlay(_pillHwnd, clickThrough: false);
        Win.MakeOverlay(_frameHwnd, clickThrough: true);

        _frame.Show();
        _pill.Show();
        Reposition();

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    private void Tick()
    {
        var t = DateTime.Now - _t0;
        _time.Text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        _dot.Opacity = t.Seconds % 2 == 0 ? 1 : 0.35;
        Reposition();
    }

    private void Reposition()
    {
        var r = _region();
        var vs = Forms.SystemInformation.VirtualScreen;
        const int pad = 3;

        Native.SetWindowPos(_frameHwnd, Native.HWND_TOPMOST, r.X - pad, r.Y - pad, r.Width + 2 * pad, r.Height + 2 * pad, SWP_NOACTIVATE | SWP_SHOWWINDOW);

        var dpi = VisualTreeHelper.GetDpi(_pill).DpiScaleX;
        var pw = (int)Math.Ceiling(_pill.ActualWidth * dpi);
        var ph = (int)Math.Ceiling(_pill.ActualHeight * dpi);
        var x = r.X + r.Width / 2 - pw / 2;
        var y = r.Bottom + 12;
        if (y + ph > vs.Bottom) y = r.Top - ph - 12;
        if (y < vs.Top) y = r.Bottom - ph - 12;
        x = Math.Clamp(x, vs.Left, Math.Max(vs.Left, vs.Right - pw));
        Native.SetWindowPos(_pillHwnd, Native.HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void Dispose()
    {
        _timer.Stop();
        try { _pill.Close(); } catch { }
        try { _frame.Close(); } catch { }
    }
}
