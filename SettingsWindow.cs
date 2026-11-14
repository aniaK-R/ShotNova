using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ShotNova;

/// <summary>Janela para o usuário escolher o atalho global de captura.</summary>
public sealed class SettingsWindow : Window
{
    private readonly TextBox _box = new()
    {
        IsReadOnly = true,
        IsReadOnlyCaretVisible = false,
        FontSize = 20,
        FontWeight = FontWeights.SemiBold,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Padding = new Thickness(8),
        Cursor = Cursors.Hand
    };
    private readonly TextBlock _msg = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Func<uint, uint, string?> _apply;

    private uint _mods;
    private uint _vk;

    /// <param name="apply">Tenta registrar o atalho; devolve null se deu certo, ou a mensagem de erro.</param>
    public SettingsWindow(Settings current, Func<uint, uint, string?> apply)
    {
        _apply = apply;
        _mods = current.HotkeyModifiers;
        _vk = current.HotkeyVk;

        Title = "ShotNova - Configurações";
        Background = (Brush)System.Windows.Application.Current.Resources["BgBrush"];
        Foreground = (Brush)System.Windows.Application.Current.Resources["TextBrush"];
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(this);
        _box.Background = (Brush)System.Windows.Application.Current.Resources["PanelBrush"];
        _box.Foreground = Brushes.White;
        _box.BorderBrush = (Brush)System.Windows.Application.Current.Resources["AccentBrush"];
        _box.BorderThickness = new Thickness(1.5);
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Atalho para capturar a área",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Clique na caixa abaixo e pressione a combinação desejada (ex.: Ctrl+PrintScreen).",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Foreground = (Brush)System.Windows.Application.Current.Resources["SubtleBrush"]
        });
        panel.Children.Add(_box);
        panel.Children.Add(_msg);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var def = new Button { Content = "Padrão (PrintScreen)", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button
        {
            Content = "Salvar",
            Padding = new Thickness(18, 7, 18, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)System.Windows.Application.Current.Resources["Primary"]
        };
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
        def.Click += (_, _) => { _mods = 0; _vk = 0x2C; Refresh(); };
        save.Click += (_, _) => Save();
        buttons.Children.Add(def);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        _box.PreviewKeyDown += OnKeyDown;
        _box.PreviewKeyUp += OnKeyUp;
        Loaded += (_, _) => { Refresh(); _box.Focus(); };
    }

    private void Refresh() => _box.Text = Settings.Format(_mods, _vk);

    // Print Screen só gera KeyUp no WPF; as demais teclas geram KeyDown.
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Snapshot) { e.Handled = true; return; }
        Capture(key);
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Snapshot) return;
        Capture(key);
        e.Handled = true;
    }

    private void Capture(Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
            return; // só modificador: espera a tecla principal

        uint mods = 0;
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= 1;
        if (m.HasFlag(ModifierKeys.Control)) mods |= 2;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= 4;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= 8;

        var isFn = key >= Key.F1 && key <= Key.F24;
        if (mods == 0 && !isFn && key != Key.Snapshot && key != Key.Pause && key != Key.Scroll)
        {
            _msg.Foreground = Brushes.Firebrick;
            _msg.Text = "Use Ctrl, Alt ou Shift junto com a tecla, para não atrapalhar a digitação.";
            return;
        }

        _mods = mods;
        _vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        _msg.Text = "";
        Refresh();
    }

    private void Save()
    {
        var error = _apply(_mods, _vk);
        if (error is null)
        {
            Close();
            return;
        }
        _msg.Foreground = Brushes.Firebrick;
        _msg.Text = error;
    }
}
