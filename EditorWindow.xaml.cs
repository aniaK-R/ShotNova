using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Shapes = System.Windows.Shapes;

namespace ShotNova;

public partial class EditorWindow : Window
{
    private enum Tool { Pen, Arrow, Rect, Highlight, Text }

    private Tool _tool = Tool.Arrow;
    private Color _color = (Color)ColorConverter.ConvertFromString("#E53935");
    private readonly List<UIElement> _undo = new();
    private UIElement? _current;
    private Point _origin;
    private TextBox? _editing;

    public EditorWindow(BitmapSource image)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(this);
        Load(image);
    }

    public static EditorWindow FromFile(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(path);
        bi.EndInit();
        bi.Freeze();
        var w = new EditorWindow(bi) { Title = "ShotNova - " + System.IO.Path.GetFileName(path) };
        return w;
    }

    private void Load(BitmapSource image)
    {
        Img.Source = image;
        Surface.Width = image.PixelWidth;
        Surface.Height = image.PixelHeight;
        Loaded += (_, _) => ApplyScale();
        Status.Text = $"{image.PixelWidth} × {image.PixelHeight} px";
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyScale();
    }

    /// <summary>Mostra a imagem 1:1 em pixels físicos, independente da escala do Windows.</summary>
    private void ApplyScale()
    {
        var s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Holder.LayoutTransform = new ScaleTransform(1 / s, 1 / s);
    }

    // ---------- ferramentas ----------

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        var b = (ToggleButton)sender;
        _tool = Enum.Parse<Tool>((string)b.Tag);
        foreach (var t in new[] { TPen, TArrow, TRect, THigh, TText })
            t.IsChecked = ReferenceEquals(t, b);
        CommitText();
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        var b = (ToggleButton)sender;
        _color = (Color)ColorConverter.ConvertFromString((string)b.Tag);
        foreach (var t in Swatches.Children.OfType<ToggleButton>())
            t.IsChecked = ReferenceEquals(t, b);
        Status.Text = "Cor: " + b.ToolTip;
    }

    private Brush Stroke(bool translucent = false)
    {
        var c = translucent ? Color.FromArgb(0x70, _color.R, _color.G, _color.B) : _color;
        var br = new SolidColorBrush(c);
        br.Freeze();
        return br;
    }

    private void Ink_Down(object sender, MouseButtonEventArgs e)
    {
        CommitText();
        _origin = e.GetPosition(Ink);
        var t = Thick.Value;

        switch (_tool)
        {
            case Tool.Pen:
                _current = new Shapes.Polyline
                {
                    Stroke = Stroke(), StrokeThickness = t,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    Points = new PointCollection { _origin }
                };
                break;
            case Tool.Highlight:
                _current = new Shapes.Polyline
                {
                    Stroke = Stroke(true), StrokeThickness = t * 4 + 8,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat,
                    Points = new PointCollection { _origin }
                };
                break;
            case Tool.Rect:
                _current = new Shapes.Rectangle { Stroke = Stroke(), StrokeThickness = t };
                Canvas.SetLeft(_current, _origin.X);
                Canvas.SetTop(_current, _origin.Y);
                break;
            case Tool.Arrow:
                _current = new Shapes.Path
                {
                    Stroke = Stroke(), StrokeThickness = t,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                };
                break;
            case Tool.Text:
                BeginText(_origin);
                return;
        }

        if (_current is not null)
        {
            Ink.Children.Add(_current);
            Ink.CaptureMouse();
        }
    }

    private void Ink_Move(object sender, MouseEventArgs e)
    {
        if (_current is null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Ink);

        switch (_current)
        {
            case Shapes.Polyline pl:
                pl.Points.Add(p);
                break;
            case Shapes.Rectangle r:
                Canvas.SetLeft(r, Math.Min(_origin.X, p.X));
                Canvas.SetTop(r, Math.Min(_origin.Y, p.Y));
                r.Width = Math.Abs(p.X - _origin.X);
                r.Height = Math.Abs(p.Y - _origin.Y);
                break;
            case Shapes.Path path:
                path.Data = ArrowGeometry(_origin, p, Thick.Value);
                break;
        }
    }

    private void Ink_Up(object sender, MouseButtonEventArgs e)
    {
        Ink.ReleaseMouseCapture();
        if (_current is null) return;
        var keep = _current switch
        {
            Shapes.Polyline pl => pl.Points.Count > 1,
            Shapes.Rectangle r => r.Width > 2 && r.Height > 2,
            Shapes.Path p => p.Data is not null,
            _ => true
        };
        if (keep) _undo.Add(_current);
        else Ink.Children.Remove(_current);
        _current = null;
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

    // ---------- texto ----------

    private void BeginText(Point at)
    {
        var tb = new TextBox
        {
            Foreground = Stroke(),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            FontSize = 14 + Thick.Value * 2,
            FontWeight = FontWeights.SemiBold,
            AcceptsReturn = true,
            MinWidth = 40,
            Padding = new Thickness(2),
            CaretBrush = Stroke()
        };
        Canvas.SetLeft(tb, at.X);
        Canvas.SetTop(tb, at.Y);
        tb.LostKeyboardFocus += (_, _) => CommitText();
        tb.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { CommitText(); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { Ink.Children.Remove(tb); _editing = null; ke.Handled = true; }
        };
        Ink.Children.Add(tb);
        _editing = tb;
        tb.Dispatcher.BeginInvoke(new Action(() => tb.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CommitText()
    {
        var tb = _editing;
        if (tb is null) return;
        _editing = null;
        var text = tb.Text;
        var idx = Ink.Children.IndexOf(tb);
        if (idx < 0) return;
        Ink.Children.RemoveAt(idx);
        if (string.IsNullOrWhiteSpace(text)) return;

        var tbk = new TextBlock
        {
            Text = text,
            Foreground = tb.Foreground,
            FontSize = tb.FontSize,
            FontWeight = tb.FontWeight,
            Padding = new Thickness(3)
        };
        Canvas.SetLeft(tbk, Canvas.GetLeft(tb));
        Canvas.SetTop(tbk, Canvas.GetTop(tb));
        Ink.Children.Insert(idx, tbk);
        _undo.Add(tbk);
    }

    // ---------- ações ----------

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        CommitText();
        if (_undo.Count == 0) return;
        Ink.Children.Remove(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
    }

    private BitmapSource Render()
    {
        CommitText();
        var w = (int)Surface.Width;
        var h = (int)Surface.Height;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(Surface), null, new Rect(0, 0, w, h));
        rtb.Render(dv);
        rtb.Freeze();
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

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetImage(Render());
            Status.Text = "Copiado para a área de transferência.";
        }
        catch (Exception ex)
        {
            Status.Text = "Falha ao copiar: " + ex.Message;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var st = Settings.Load();
        var folder = st.SaveFolder();
        var dlg = new SaveFileDialog
        {
            Filter = "PNG (*.png)|*.png",
            FileName = st.NextName(folder),
            InitialDirectory = folder,
            DefaultExt = ".png"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            WritePng(Render(), dlg.FileName);
            st.RememberSave(dlg.FileName);
            Status.Text = "Salvo em " + dlg.FileName;
        }
        catch (Exception ex)
        {
            Status.Text = "Falha ao salvar: " + ex.Message;
        }
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = System.IO.Path.Combine(Paths.History, Stamp());
            WritePng(Render(), path);
            Status.Text = "Guardado no histórico: " + path;
        }
        catch (Exception ex)
        {
            Status.Text = "Falha ao guardar: " + ex.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_editing is not null && e.Key != Key.Escape) return; // deixa digitar
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (e.Key == Key.Escape) Close();
        else if (ctrl && e.Key == Key.Z) Undo();
        else if (ctrl && e.Key == Key.C) Copy_Click(this, e);
        else if (ctrl && e.Key == Key.S) Save_Click(this, e);
        else if (ctrl && e.Key == Key.H) History_Click(this, e);
    }
}

internal static class Paths
{
    public static string Captures => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ShotNova");
    public static string History => System.IO.Path.Combine(Captures, "Historico");
}
