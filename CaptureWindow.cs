using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Shapes = System.Windows.Shapes;

namespace ShotNova;

/// <summary>
/// Captura no estilo Lightshot: a tela congela, o usuário seleciona a área e edita ali mesmo
/// (alças para mover/redimensionar, barra vertical de ferramentas, barra horizontal de ações).
/// Coordenadas internas em pixels físicos; as barras ficam numa camada em DIPs (tamanho normal).
/// </summary>
public sealed class CaptureWindow : Window
{
    private enum Mode { None, Selecting, Moving, Resizing, Drawing }
    private enum Tool { None, Pen, Line, Arrow, Rect, Highlight, Text }

    private readonly BitmapSource _shot;
    private readonly System.Drawing.Rectangle _vs;

    // camada em pixels físicos (escalada por 1/dpi)
    private readonly Grid _pxHost = new();
    private readonly Canvas _canvas = new();
    private readonly Shapes.Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)) };
    private readonly Canvas _ink = new() { Background = Brushes.Transparent };
    private readonly Shapes.Rectangle _edgeDark = new() { Stroke = Brushes.Black, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Shapes.Rectangle _edgeLight = new()
    {
        Stroke = Brushes.White,
        StrokeDashArray = new DoubleCollection { 4, 4 },
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };
    private readonly Shapes.Rectangle[] _handles = new Shapes.Rectangle[8];

    // camada em DIPs (barras)
    private readonly Canvas _ui = new();
    private readonly Border _sizeBox = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x17, 0x18, 0x1B)),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(8, 3, 8, 3),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12 };
    private readonly Border _hint;
    private Border _tools = null!;
    private Border _actions = null!;
    private Border _palette = null!;
    private Shapes.Ellipse? _colorDot;
    private readonly Dictionary<Tool, ToggleButton> _toolButtons = new();
    private Slider _thick = null!;

    private double _s = 1;
    private bool _hasSel;
    private Rect _sel;
    private Mode _mode;
    private Point _anchor;
    private Rect _selAtDown;
    private int _handle;

    private Tool _tool = Tool.None;
    private Color _color = Color.FromRgb(0xE5, 0x39, 0x35);
    private readonly List<UIElement> _undo = new();
    private UIElement? _cur;
    private TextBox? _editing;

    public event Action<string>? Info;

    public CaptureWindow(BitmapSource shot, System.Drawing.Rectangle virtualScreen)
    {
        _shot = shot;
        _vs = virtualScreen;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = virtualScreen.X;
        Top = virtualScreen.Y;
        Width = 100;
        Height = 100;
        Title = "ShotNova - captura";
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _canvas.Width = shot.PixelWidth;
        _canvas.Height = shot.PixelHeight;
        _canvas.Children.Add(new Image { Source = shot, Width = shot.PixelWidth, Height = shot.PixelHeight, Stretch = Stretch.Fill, IsHitTestVisible = false });
        _canvas.Children.Add(_dim);
        _ink.Width = shot.PixelWidth;
        _ink.Height = shot.PixelHeight;
        _canvas.Children.Add(_ink);
        _canvas.Children.Add(_edgeDark);
        _canvas.Children.Add(_edgeLight);
        for (var i = 0; i < 8; i++)
        {
            _handles[i] = new Shapes.Rectangle
            {
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            _canvas.Children.Add(_handles[i]);
        }
        _pxHost.Children.Add(_canvas);

        _sizeBox.Child = _sizeText;
        _ui.Children.Add(_sizeBox);

        _hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x17, 0x18, 0x1B)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Arraste para selecionar a área  •  Esc cancela",
                Foreground = Brushes.White,
                FontSize = 14
            }
        };
        Canvas.SetLeft(_hint, 24);
        Canvas.SetTop(_hint, 24);
        _ui.Children.Add(_hint);

        BuildToolbars();

        var root = new Grid();
        root.Children.Add(_pxHost);
        root.Children.Add(_ui);
        Content = root;

        SourceInitialized += (_, _) => Place();
        Loaded += (_, _) => { ApplyScale(); Activate(); Focus(); };
        LayoutAll();
    }

    // ---------- barras ----------

    private static Style Res(string key) => (Style)System.Windows.Application.Current.Resources[key];
    private static Brush Brush_(string key) => (Brush)System.Windows.Application.Current.Resources[key];

    private static Shapes.Path MakeIcon(string data) => new()
    {
        Style = Res("Ico"),
        Data = Geometry.Parse(data),
        Margin = new Thickness(0),
        Width = 18,
        Height = 18
    };

    private static Border Bar(UIElement content) => new()
    {
        Background = Brush_("PanelBrush"),
        BorderBrush = Brush_("LineBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(4),
        Visibility = Visibility.Collapsed,
        Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.5, Color = Colors.Black },
        Child = content
    };

    private ToggleButton ToolButton(Tool t, string data, string tip)
    {
        var b = new ToggleButton { Content = MakeIcon(data), ToolTip = tip, Padding = new Thickness(8), Margin = new Thickness(0, 1, 0, 1) };
        b.Click += (_, _) => SetTool(_tool == t ? Tool.None : t);
        _toolButtons[t] = b;
        return b;
    }

    private Button ActionButton(string data, string tip, Action act, bool primary = false)
    {
        var b = new Button { Content = MakeIcon(data), ToolTip = tip, Padding = new Thickness(8), Margin = new Thickness(1, 0, 1, 0) };
        if (primary) b.Style = Res("Primary");
        b.Click += (_, _) => act();
        return b;
    }

    private void BuildToolbars()
    {
        var v = new StackPanel { Orientation = Orientation.Vertical };
        v.Children.Add(ToolButton(Tool.Pen, "M3,21 L4,16 L16,4 L20,8 L8,20 Z M14,6 L18,10", "Caneta"));
        v.Children.Add(ToolButton(Tool.Line, "M4,20 L20,4", "Linha"));
        v.Children.Add(ToolButton(Tool.Arrow, "M4,20 L19,5 M10,5 L19,5 L19,14", "Seta"));
        v.Children.Add(ToolButton(Tool.Rect, "M4,6 L20,6 L20,18 L4,18 Z", "Caixa"));
        v.Children.Add(ToolButton(Tool.Highlight, "M9,15 L17,7 L21,11 L13,19 Z M9,15 L7,21 L13,19 M3,21 L8,21", "Marca-texto"));
        v.Children.Add(ToolButton(Tool.Text, "M5,6 L5,4 L19,4 L19,6 M12,4 L12,20 M9,20 L15,20", "Texto"));

        _colorDot = new Shapes.Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(_color), Stroke = Brushes.White, StrokeThickness = 1.5 };
        var colorBtn = new Button { Content = _colorDot, ToolTip = "Cor e espessura", Padding = new Thickness(8), Margin = new Thickness(0, 1, 0, 1) };
        colorBtn.Click += (_, _) => { _palette.Visibility = _palette.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; LayoutAll(); };
        v.Children.Add(colorBtn);
        v.Children.Add(new Border { Height = 1, Margin = new Thickness(4, 3, 4, 3), Background = Brush_("LineBrush") });
        v.Children.Add(ActionButton("M8,5 L3,10 L8,15 M3,10 L14,10 A6,6 0 0 1 14,22 L9,22", "Desfazer (Ctrl+Z)", Undo));
        _tools = Bar(v);
        _ui.Children.Add(_tools);

        var h = new StackPanel { Orientation = Orientation.Horizontal };
        h.Children.Add(ActionButton("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M12,7 L12,12 L16,14", "Guardar no histórico (Ctrl+H)", DoHistory));
        h.Children.Add(ActionButton("M5,4 L16,4 L20,8 L20,20 L5,20 Z M8,4 L8,9 L15,9 L15,4 M8,20 L8,14 L17,14 L17,20", "Salvar como… (Ctrl+S)", DoSave));
        h.Children.Add(ActionButton("M8,8 L20,8 L20,21 L8,21 Z M16,8 L16,4 L4,4 L4,17 L8,17", "Copiar (Ctrl+C ou Enter)", DoCopy, primary: true));
        h.Children.Add(ActionButton("M5,5 L19,19 M19,5 L5,19", "Fechar (Esc)", Close));
        _actions = Bar(h);
        _ui.Children.Add(_actions);

        // paleta de cor e espessura (abre pelo botão de cor)
        var pal = new StackPanel { Orientation = Orientation.Vertical };
        var sw = new WrapPanel { Width = 6 * 30 };
        foreach (var hex in new[] { "#E53935", "#FDD835", "#43A047", "#1E88E5", "#111111", "#FFFFFF" })
        {
            var b = new ToggleButton { Style = Res("Swatch"), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), Tag = hex, IsChecked = hex == "#E53935" };
            b.Click += (s, _) =>
            {
                var me = (ToggleButton)s!;
                _color = (Color)ColorConverter.ConvertFromString((string)me.Tag);
                foreach (var t in sw.Children.OfType<ToggleButton>()) t.IsChecked = ReferenceEquals(t, me);
                if (_colorDot is not null) _colorDot.Fill = new SolidColorBrush(_color);
            };
            sw.Children.Add(b);
        }
        pal.Children.Add(sw);
        _thick = new Slider { Minimum = 1, Maximum = 12, Value = 3, Width = 170, Margin = new Thickness(4, 8, 4, 4), ToolTip = "Espessura" };
        pal.Children.Add(_thick);
        _palette = Bar(pal);
        _palette.Padding = new Thickness(8);
        _ui.Children.Add(_palette);
    }

    private void SetTool(Tool t)
    {
        CommitText();
        _tool = t;
        foreach (var kv in _toolButtons) kv.Value.IsChecked = kv.Key == t;
    }

    // ---------- escala e posição ----------

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
        LayoutAll();
    }

    private void Place()
    {
        var h = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(h, Native.HWND_TOPMOST, _vs.X, _vs.Y, _vs.Width, _vs.Height, Native.SWP_SHOWWINDOW);
    }

    // ---------- desenho da seleção ----------

    private void LayoutAll()
    {
        var W = _shot.PixelWidth;
        var H = _shot.PixelHeight;
        Geometry full = new RectangleGeometry(new Rect(0, 0, W, H));

        if (!_hasSel)
        {
            _dim.Data = full;
            _ink.Clip = null;
            _edgeDark.Visibility = _edgeLight.Visibility = Visibility.Collapsed;
            foreach (var hd in _handles) hd.Visibility = Visibility.Collapsed;
            _sizeBox.Visibility = Visibility.Collapsed;
            _tools.Visibility = _actions.Visibility = _palette.Visibility = Visibility.Collapsed;
            _hint.Visibility = Visibility.Visible;
            return;
        }

        _hint.Visibility = Visibility.Collapsed;
        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(_sel));
        _ink.Clip = new RectangleGeometry(_sel);

        foreach (var e in new[] { _edgeDark, _edgeLight })
        {
            e.Visibility = Visibility.Visible;
            e.StrokeThickness = _s;
            e.Width = _sel.Width;
            e.Height = _sel.Height;
            Canvas.SetLeft(e, _sel.X);
            Canvas.SetTop(e, _sel.Y);
        }

        var hs = 7 * _s;
        var pts = HandleCenters();
        for (var i = 0; i < 8; i++)
        {
            var hd = _handles[i];
            hd.Visibility = Visibility.Visible;
            hd.Width = hd.Height = hs;
            hd.StrokeThickness = _s;
            Canvas.SetLeft(hd, pts[i].X - hs / 2);
            Canvas.SetTop(hd, pts[i].Y - hs / 2);
        }

        // rótulo de tamanho e barras (em DIPs)
        var selD = new Rect(_sel.X / _s, _sel.Y / _s, _sel.Width / _s, _sel.Height / _s);
        var bw = W / _s;
        var bh = H / _s;

        _sizeText.Text = $"{(int)Math.Round(_sel.Width)} × {(int)Math.Round(_sel.Height)}";
        _sizeBox.Visibility = Visibility.Visible;
        Canvas.SetLeft(_sizeBox, Math.Clamp(selD.X, 0, Math.Max(0, bw - 90)));
        Canvas.SetTop(_sizeBox, selD.Y >= 30 ? selD.Y - 28 : selD.Y + 6);

        var show = _mode == Mode.None;
        _tools.Visibility = _actions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) { _palette.Visibility = Visibility.Collapsed; return; }

        _tools.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var t = _tools.DesiredSize;
        var a = _actions.DesiredSize;

        var tx = selD.Right + 8;
        if (tx + t.Width > bw) tx = selD.Left - 8 - t.Width;
        if (tx < 0) tx = selD.Right - t.Width - 8;
        var ty = Math.Clamp(selD.Top, 0, Math.Max(0, bh - t.Height));
        Canvas.SetLeft(_tools, tx);
        Canvas.SetTop(_tools, ty);

        var ay = selD.Bottom + 8;
        if (ay + a.Height > bh) ay = selD.Top - 8 - a.Height;
        if (ay < 0) ay = selD.Bottom - a.Height - 8;
        var ax = Math.Clamp(selD.Right - a.Width, 0, Math.Max(0, bw - a.Width));
        Canvas.SetLeft(_actions, ax);
        Canvas.SetTop(_actions, ay);

        if (_palette.Visibility == Visibility.Visible)
        {
            _palette.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var p = _palette.DesiredSize;
            var px = tx + t.Width + 6;
            if (px + p.Width > bw) px = tx - 6 - p.Width;
            Canvas.SetLeft(_palette, Math.Max(0, px));
            Canvas.SetTop(_palette, ty);
        }
    }

    private Point[] HandleCenters()
    {
        var r = _sel;
        var cx = r.X + r.Width / 2;
        var cy = r.Y + r.Height / 2;
        return new[]
        {
            new Point(r.Left, r.Top), new Point(cx, r.Top), new Point(r.Right, r.Top),
            new Point(r.Right, cy), new Point(r.Right, r.Bottom), new Point(cx, r.Bottom),
            new Point(r.Left, r.Bottom), new Point(r.Left, cy)
        };
    }

    private int HitHandle(Point p)
    {
        var tol = 8 * _s;
        var pts = HandleCenters();
        for (var i = 0; i < 8; i++)
            if (Math.Abs(p.X - pts[i].X) <= tol && Math.Abs(p.Y - pts[i].Y) <= tol) return i;
        return -1;
    }

    private static Cursor HandleCursor(int h) => h switch
    {
        0 or 4 => Cursors.SizeNWSE,
        2 or 6 => Cursors.SizeNESW,
        1 or 5 => Cursors.SizeNS,
        _ => Cursors.SizeWE
    };

    private bool InUi(object? src)
    {
        var d = src as DependencyObject;
        while (d is not null)
        {
            if (ReferenceEquals(d, _ui)) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private Point Pos(MouseEventArgs e) => e.GetPosition(_canvas);

    // ---------- mouse ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (InUi(e.OriginalSource)) return;
        var p = Pos(e);
        CommitText();

        if (!_hasSel) { StartSelecting(p); return; }

        var h = HitHandle(p);
        if (h >= 0)
        {
            _mode = Mode.Resizing; _handle = h; _selAtDown = _sel; _anchor = p;
            _palette.Visibility = Visibility.Collapsed;
            CaptureMouse(); LayoutAll();
        }
        else if (_sel.Contains(p))
        {
            if (_tool == Tool.None)
            {
                _mode = Mode.Moving; _selAtDown = _sel; _anchor = p;
                _palette.Visibility = Visibility.Collapsed;
                CaptureMouse(); LayoutAll();
            }
            else BeginDraw(p);
        }
        else
        {
            // clique fora: recomeça a seleção (descarta anotações)
            _ink.Children.Clear();
            _undo.Clear();
            StartSelecting(p);
        }
    }

    private void StartSelecting(Point p)
    {
        _mode = Mode.Selecting;
        _anchor = p;
        _sel = new Rect(p, p);
        _hasSel = true;
        CaptureMouse();
        LayoutAll();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = Pos(e);
        var W = _shot.PixelWidth;
        var H = _shot.PixelHeight;

        switch (_mode)
        {
            case Mode.Selecting:
                _sel = new Rect(_anchor, new Point(Math.Clamp(p.X, 0, W), Math.Clamp(p.Y, 0, H)));
                LayoutAll();
                return;
            case Mode.Moving:
            {
                var dx = Math.Clamp(p.X - _anchor.X, -_selAtDown.X, W - _selAtDown.Right);
                var dy = Math.Clamp(p.Y - _anchor.Y, -_selAtDown.Y, H - _selAtDown.Bottom);
                _sel = new Rect(_selAtDown.X + dx, _selAtDown.Y + dy, _selAtDown.Width, _selAtDown.Height);
                LayoutAll();
                return;
            }
            case Mode.Resizing:
            {
                var d = p - _anchor;
                double l = _selAtDown.Left, t = _selAtDown.Top, r = _selAtDown.Right, b = _selAtDown.Bottom;
                switch (_handle)
                {
                    case 0: l += d.X; t += d.Y; break;
                    case 1: t += d.Y; break;
                    case 2: r += d.X; t += d.Y; break;
                    case 3: r += d.X; break;
                    case 4: r += d.X; b += d.Y; break;
                    case 5: b += d.Y; break;
                    case 6: l += d.X; b += d.Y; break;
                    case 7: l += d.X; break;
                }
                l = Math.Clamp(l, 0, W); r = Math.Clamp(r, 0, W);
                t = Math.Clamp(t, 0, H); b = Math.Clamp(b, 0, H);
                _sel = new Rect(Math.Min(l, r), Math.Min(t, b), Math.Abs(r - l), Math.Abs(b - t));
                LayoutAll();
                return;
            }
            case Mode.Drawing:
                ContinueDraw(p);
                return;
        }

        // cursor conforme o que está sob o mouse
        if (InUi(e.OriginalSource)) return;
        if (!_hasSel) { Cursor = Cursors.Cross; return; }
        var h = HitHandle(p);
        Cursor = h >= 0 ? HandleCursor(h)
               : _sel.Contains(p) ? (_tool == Tool.None ? Cursors.SizeAll : Cursors.Cross)
               : Cursors.Cross;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var m = _mode;
        _mode = Mode.None;
        ReleaseMouseCapture();

        if (m == Mode.Drawing) { EndDraw(); return; }
        if (m == Mode.None) return;

        if (_sel.Width < 4 || _sel.Height < 4)
        {
            _hasSel = false;
            _ink.Children.Clear();
            _undo.Clear();
        }
        LayoutAll();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (InUi(e.OriginalSource)) return;
        // botão direito: desfaz a seleção; sem seleção, cancela a captura
        if (_hasSel)
        {
            _hasSel = false;
            _ink.Children.Clear();
            _undo.Clear();
            SetTool(Tool.None);
            LayoutAll();
        }
        else Close();
    }

    // ---------- desenho das anotações ----------

    private Brush Stroke(bool translucent = false)
    {
        var c = translucent ? Color.FromArgb(0x70, _color.R, _color.G, _color.B) : _color;
        var br = new SolidColorBrush(c);
        br.Freeze();
        return br;
    }

    private void BeginDraw(Point p)
    {
        var t = _thick.Value * _s;
        _anchor = p;
        switch (_tool)
        {
            case Tool.Pen:
                _cur = new Shapes.Polyline
                {
                    Stroke = Stroke(), StrokeThickness = t, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    Points = new PointCollection { p }
                };
                break;
            case Tool.Highlight:
                _cur = new Shapes.Polyline
                {
                    Stroke = Stroke(true), StrokeThickness = (t * 4 + 8 * _s), StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat,
                    Points = new PointCollection { p }
                };
                break;
            case Tool.Rect:
                _cur = new Shapes.Rectangle { Stroke = Stroke(), StrokeThickness = t };
                Canvas.SetLeft(_cur, p.X);
                Canvas.SetTop(_cur, p.Y);
                break;
            case Tool.Line:
            case Tool.Arrow:
                _cur = new Shapes.Path
                {
                    Stroke = Stroke(), StrokeThickness = t, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                };
                break;
            case Tool.Text:
                BeginText(p);
                return;
            default:
                return;
        }
        _mode = Mode.Drawing;
        _ink.Children.Add(_cur);
        CaptureMouse();
    }

    private void ContinueDraw(Point p)
    {
        switch (_cur)
        {
            case Shapes.Polyline pl:
                pl.Points.Add(p);
                break;
            case Shapes.Rectangle r:
                Canvas.SetLeft(r, Math.Min(_anchor.X, p.X));
                Canvas.SetTop(r, Math.Min(_anchor.Y, p.Y));
                r.Width = Math.Abs(p.X - _anchor.X);
                r.Height = Math.Abs(p.Y - _anchor.Y);
                break;
            case Shapes.Path path:
                path.Data = _tool == Tool.Arrow
                    ? ArrowGeometry(_anchor, p, _thick.Value * _s)
                    : new LineGeometry(_anchor, p);
                break;
        }
    }

    private void EndDraw()
    {
        if (_cur is null) return;
        var keep = _cur switch
        {
            Shapes.Polyline pl => pl.Points.Count > 1,
            Shapes.Rectangle r => r.Width > 2 && r.Height > 2,
            Shapes.Path p => p.Data is not null,
            _ => true
        };
        if (keep) _undo.Add(_cur); else _ink.Children.Remove(_cur);
        _cur = null;
    }

    private static Geometry ArrowGeometry(Point a, Point b, double thickness)
    {
        var g = new GeometryGroup();
        g.Children.Add(new LineGeometry(a, b));
        var v = a - b;
        if (v.Length < 1) return g;
        v.Normalize();
        var len = 12 + thickness * 2.5;
        foreach (var deg in new[] { 28, -28 })
        {
            var rad = deg * Math.PI / 180;
            var rot = new Vector(v.X * Math.Cos(rad) - v.Y * Math.Sin(rad),
                                 v.X * Math.Sin(rad) + v.Y * Math.Cos(rad));
            g.Children.Add(new LineGeometry(b, b + rot * len));
        }
        return g;
    }

    private void BeginText(Point at)
    {
        var tb = new TextBox
        {
            Foreground = Stroke(),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(_s),
            FontSize = (14 + _thick.Value * 2) * _s,
            FontWeight = FontWeights.SemiBold,
            AcceptsReturn = true,
            MinWidth = 40 * _s,
            Padding = new Thickness(2 * _s),
            CaretBrush = Stroke()
        };
        Canvas.SetLeft(tb, at.X);
        Canvas.SetTop(tb, at.Y);
        tb.LostKeyboardFocus += (_, _) => CommitText();
        tb.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { CommitText(); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { _ink.Children.Remove(tb); _editing = null; ke.Handled = true; Focus(); }
        };
        _ink.Children.Add(tb);
        _editing = tb;
        tb.Dispatcher.BeginInvoke(new Action(() => tb.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CommitText()
    {
        var tb = _editing;
        if (tb is null) return;
        _editing = null;
        var idx = _ink.Children.IndexOf(tb);
        if (idx < 0) return;
        _ink.Children.RemoveAt(idx);
        if (string.IsNullOrWhiteSpace(tb.Text)) { Focus(); return; }

        var tbk = new TextBlock
        {
            Text = tb.Text,
            Foreground = tb.Foreground,
            FontSize = tb.FontSize,
            FontWeight = tb.FontWeight,
            Padding = new Thickness(3 * _s)
        };
        Canvas.SetLeft(tbk, Canvas.GetLeft(tb));
        Canvas.SetTop(tbk, Canvas.GetTop(tb));
        _ink.Children.Insert(idx, tbk);
        _undo.Add(tbk);
        Focus();
    }

    private void Undo()
    {
        CommitText();
        if (_undo.Count == 0) return;
        _ink.Children.Remove(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
    }

    // ---------- ações ----------

    private BitmapSource Render()
    {
        CommitText();
        var x = (int)Math.Round(_sel.X);
        var y = (int)Math.Round(_sel.Y);
        var w = Math.Max(1, Math.Min((int)Math.Round(_sel.Width), _shot.PixelWidth - x));
        var h = Math.Max(1, Math.Min((int)Math.Round(_sel.Height), _shot.PixelHeight - y));

        var crop = new CroppedBitmap(_shot, new Int32Rect(x, y, w, h));
        var clip = _ink.Clip;
        _ink.Clip = null;

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(crop, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(_ink)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(x, y, w, h),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, w, h),
                Stretch = Stretch.Fill
            }, null, new Rect(0, 0, w, h));
        }
        rtb.Render(dv);
        rtb.Freeze();
        _ink.Clip = clip;
        return rtb;
    }

    private static void WritePng(BitmapSource bmp, string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static string Stamp() => "ShotNova_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";

    private void DoCopy()
    {
        if (!_hasSel) return;
        try
        {
            Clipboard.SetImage(Render());
            Info?.Invoke("Copiado para a área de transferência.");
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Não foi possível copiar: " + ex.Message, "ShotNova");
        }
    }

    private void DoSave()
    {
        if (!_hasSel) return;
        try
        {
            var st = Settings.Load();
            var folder = st.SaveFolder();
            var img = Render();
            var dlg = new SaveFileDialog
            {
                Filter = "PNG (*.png)|*.png",
                FileName = st.NextName(folder),
                InitialDirectory = folder,
                DefaultExt = ".png"
            };
            if (dlg.ShowDialog(this) != true) return;
            WritePng(img, dlg.FileName);
            st.RememberSave(dlg.FileName);
            Info?.Invoke("Salvo em " + dlg.FileName);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Não foi possível salvar: " + ex.Message, "ShotNova");
        }
    }

    private void DoHistory()
    {
        if (!_hasSel) return;
        try
        {
            var path = System.IO.Path.Combine(Paths.History, Stamp());
            WritePng(Render(), path);
            Info?.Invoke("Guardado no histórico.");
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Não foi possível guardar: " + ex.Message, "ShotNova");
        }
    }

    // ---------- teclado ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_editing is not null) return;
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (e.Key == Key.Escape) Close();
        else if (ctrl && e.Key == Key.Z) Undo();
        else if (ctrl && e.Key == Key.C) DoCopy();
        else if (ctrl && e.Key == Key.S) DoSave();
        else if (ctrl && e.Key == Key.H) DoHistory();
        else if (e.Key == Key.Enter && _hasSel) DoCopy();
    }
}

