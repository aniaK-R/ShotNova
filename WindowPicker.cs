using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Shapes = System.Windows.Shapes;
using Point = System.Windows.Point;

namespace ShotNova;

/// <summary>Overlay translúcido: destaca a janela sob o cursor; o clique escolhe qual gravar.</summary>
public sealed class WindowPicker : Window
{
    private readonly System.Drawing.Rectangle _vs;
    private readonly List<(IntPtr Hwnd, Rectangle Bounds)> _windows = new();
    private readonly Grid _pxHost = new();
    private readonly Canvas _canvas = new();
    private readonly Shapes.Rectangle _hl = new()
    {
        Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6)),
        Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0x3B, 0x82, 0xF6)),
        Visibility = Visibility.Collapsed
    };
    private double _s = 1;
    private IntPtr _current;

    public event Action<IntPtr, Rectangle>? Picked;

    public WindowPicker(System.Drawing.Rectangle virtualScreen)
    {
        _vs = virtualScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x01, 0, 0, 0)); // quase invisível, mas captura o mouse
        Cursor = Cursors.Hand;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = virtualScreen.X; Top = virtualScreen.Y; Width = 100; Height = 100;
        Title = "ShotNova - escolher janela";

        _canvas.Width = virtualScreen.Width;
        _canvas.Height = virtualScreen.Height;
        _hl.StrokeThickness = 3;
        _canvas.Children.Add(_hl);
        _pxHost.Children.Add(_canvas);

        var hint = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE6, 0x17, 0x18, 0x1B)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Clique na janela que deseja gravar  •  Esc cancela",
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 14,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI")
            }
        };
        var ui = new Canvas();
        Canvas.SetLeft(hint, 24);
        Canvas.SetTop(hint, 24);
        ui.Children.Add(hint);

        var root = new Grid();
        root.Children.Add(_pxHost);
        root.Children.Add(ui);
        Content = root;

        CollectWindows();
        SourceInitialized += (_, _) => Place();
        Loaded += (_, _) => { ApplyScale(); Activate(); Focus(); };
    }

    private void CollectWindows()
    {
        var me = (uint)Process.GetCurrentProcess().Id;
        Win.EnumWindows((h, _) =>
        {
            if (!Win.IsWindowVisible(h) || Win.IsIconic(h) || Win.IsCloaked(h)) return true;
            Win.GetWindowThreadProcessId(h, out var pid);
            if (pid == me) return true;

            var cls = new StringBuilder(64);
            Win.GetClassName(h, cls, cls.Capacity);
            var c = cls.ToString();
            if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;

            var title = new StringBuilder(256);
            Win.GetWindowText(h, title, title.Capacity);
            if (title.Length == 0) return true;

            if (Win.TryFrameBounds(h, out var r) && r.Width >= 40 && r.Height >= 40)
                _windows.Add((h, r)); // EnumWindows entrega de cima para baixo (ordem Z)
            return true;
        }, IntPtr.Zero);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyScale();
        Place();
    }

    private void ApplyScale()
    {
        _s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _pxHost.LayoutTransform = new ScaleTransform(1 / _s, 1 / _s);
    }

    private void Place()
    {
        var h = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(h, Native.HWND_TOPMOST, _vs.X, _vs.Y, _vs.Width, _vs.Height, Native.SWP_SHOWWINDOW);
    }

    private (IntPtr Hwnd, Rectangle Bounds)? HitTest(Point p)
    {
        var x = (int)(_vs.X + p.X);
        var y = (int)(_vs.Y + p.Y);
        foreach (var w in _windows)
            if (w.Bounds.Contains(x, y)) return w;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.GetPosition(_canvas));
        if (hit is null)
        {
            _current = IntPtr.Zero;
            _hl.Visibility = Visibility.Collapsed;
            return;
        }
        _current = hit.Value.Hwnd;
        var b = hit.Value.Bounds;
        _hl.Visibility = Visibility.Visible;
        _hl.Width = b.Width;
        _hl.Height = b.Height;
        Canvas.SetLeft(_hl, b.X - _vs.X);
        Canvas.SetTop(_hl, b.Y - _vs.Y);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var hit = HitTest(e.GetPosition(_canvas));
        if (hit is null) return;
        var (h, b) = hit.Value;
        Close();
        Picked?.Invoke(h, b);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }
}
