using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ShotNova;

public partial class App : System.Windows.Application
{
    private const int HotkeyId = 1;
    private const uint MOD_NOREPEAT = 0x4000;

    private Settings _settings = new();
    private SettingsWindow? _settingsWindow;
    private Mutex? _mutex;
    private EventWaitHandle? _captureEvent;
    private Forms.NotifyIcon? _tray;
    private HwndSource? _hotkeyWindow;
    private bool _capturing;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var file = e.Args.FirstOrDefault(a => !a.StartsWith("--"));
        var captureNow = e.Args.Contains("--capture");

        // Abrir um arquivo de imagem (menu de contexto): editor avulso, sem bandeja.
        if (file is not null && File.Exists(file))
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            EditorWindow.FromFile(file).Show();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _mutex = new Mutex(true, "ShotNova.Single", out var first);
        if (!first)
        {
            // Já existe uma instância: pede para ela capturar e sai.
            if (captureNow)
            {
                try { EventWaitHandle.OpenExisting("ShotNova.CaptureEvt").Set(); } catch { }
            }
            Shutdown();
            return;
        }

        _captureEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "ShotNova.CaptureEvt");
        new Thread(() =>
        {
            while (_captureEvent.WaitOne())
                Dispatcher.BeginInvoke(new Action(StartCapture));
        }) { IsBackground = true }.Start();

        CreateTray();
        RegisterHotkeys();
        if (captureNow) Dispatcher.BeginInvoke(new Action(StartCapture));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_hotkeyWindow is not null)
        {
            Native.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
            _hotkeyWindow.Dispose();
        }
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnExit(e);
    }

    // ---------- atalho global ----------

    private void RegisterHotkeys()
    {
        var p = new HwndSourceParameters("ShotNovaHotkeys") { ParentWindow = Native.HWND_MESSAGE, Width = 0, Height = 0 };
        _hotkeyWindow = new HwndSource(p);
        _hotkeyWindow.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == Native.WM_HOTKEY)
            {
                handled = true;
                Dispatcher.BeginInvoke(new Action(StartCapture));
            }
            return IntPtr.Zero;
        });

        _settings = Settings.Load();
        var ok = Native.RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, _settings.HotkeyModifiers | MOD_NOREPEAT, _settings.HotkeyVk);
        UpdateTrayText();
        var msg = ok
            ? $"{_settings.HotkeyText} captura a área."
            : $"{_settings.HotkeyText} está ocupado por outro app. Abra Configurações na bandeja e escolha outro atalho.";
        _tray?.ShowBalloonTip(3000, "ShotNova", msg, ok ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
    }

    private void UpdateTrayText()
    {
        if (_tray is not null) _tray.Text = "ShotNova (" + _settings.HotkeyText + ")";
    }

    /// <summary>Troca o atalho. Devolve null se deu certo, ou o motivo da falha (o atalho antigo é mantido).</summary>
    private string? ApplyHotkey(uint mods, uint vk)
    {
        if (_hotkeyWindow is null) return "O app ainda não está pronto.";
        var h = _hotkeyWindow.Handle;
        Native.UnregisterHotKey(h, HotkeyId);
        if (Native.RegisterHotKey(h, HotkeyId, mods | MOD_NOREPEAT, vk))
        {
            _settings = Settings.Load(); // preserva pasta/contador gravados pelo salvar
            _settings.HotkeyModifiers = mods;
            _settings.HotkeyVk = vk;
            try { _settings.Save(); }
            catch (Exception ex) { return "Atalho aplicado, mas não foi possível salvar: " + ex.Message; }
            UpdateTrayText();
            _tray?.ShowBalloonTip(2500, "ShotNova", $"Novo atalho: {_settings.HotkeyText}", Forms.ToolTipIcon.Info);
            return null;
        }

        // falhou: restaura o anterior
        Native.RegisterHotKey(h, HotkeyId, _settings.HotkeyModifiers | MOD_NOREPEAT, _settings.HotkeyVk);
        return $"{Settings.Format(mods, vk)} já está em uso por outro programa ou pelo Windows. Escolha outra combinação.";
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(_settings, ApplyHotkey);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    // ---------- bandeja e menu ----------

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Capturar área", null, (_, _) => Dispatcher.BeginInvoke(new Action(StartCapture)));
        menu.Items.Add("Abrir imagem…", null, (_, _) => Dispatcher.BeginInvoke(new Action(OpenImage)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Abrir pasta de capturas", null, (_, _) => OpenFolder(Paths.Captures));
        menu.Items.Add("Abrir pasta do histórico", null, (_, _) => OpenFolder(Paths.History));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Configurações (atalho)…", null, (_, _) => Dispatcher.BeginInvoke(new Action(OpenSettings)));
        var shellItem = new Forms.ToolStripMenuItem("Integrar ao menu de contexto do Windows") { CheckOnClick = false };
        shellItem.Click += (_, _) => ToggleShellIntegration();
        menu.Items.Add(shellItem);
        menu.Opening += (_, _) => shellItem.Checked = ShellIntegrationInstalled();
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Dispatcher.BeginInvoke(new Action(Shutdown)));

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "ShotNova",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.MouseClick += (_, ev) =>
        {
            if (ev.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(new Action(StartCapture));
        };
    }

    /// <summary>Ícone da bandeja a partir do .ico embutido (quadro mais próximo de 32 px).</summary>
    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var dec = new System.Windows.Media.Imaging.IconBitmapDecoder(
                new Uri("pack://application:,,,/assets/ShotNova.ico"),
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = dec.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 32)).First();
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(frame);
            using var ms = new MemoryStream();
            enc.Save(ms);
            ms.Position = 0;
            using var bmp = new Drawing.Bitmap(ms);
            return Drawing.Icon.FromHandle(bmp.GetHicon());
        }
        catch
        {
            return MakeIcon();
        }
    }

    private static Drawing.Icon MakeIcon()
    {
        using var b = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(b))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x2F, 0x6F, 0xED));
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var pen = new Drawing.Pen(Drawing.Color.White, 3);
            g.DrawLines(pen, new[] { new Drawing.Point(9, 15), new Drawing.Point(9, 9), new Drawing.Point(15, 9) });
            g.DrawLines(pen, new[] { new Drawing.Point(23, 17), new Drawing.Point(23, 23), new Drawing.Point(17, 23) });
        }
        return Drawing.Icon.FromHandle(b.GetHicon());
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void OpenImage()
    {
        var dlg = new OpenFileDialog { Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
        if (dlg.ShowDialog() == true) EditorWindow.FromFile(dlg.FileName).Show();
    }

    // ---------- captura ----------

    private void StartCapture()
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            var shot = ScreenGrabber.GrabVirtualScreen(out var vs);
            var win = new CaptureWindow(shot, vs);
            win.Info += msg => _tray?.ShowBalloonTip(1800, "ShotNova", msg, Forms.ToolTipIcon.Info);
            win.Closed += (_, _) => _capturing = false;
            win.Show();
        }
        catch (Exception ex)
        {
            _capturing = false;
            _tray?.ShowBalloonTip(4000, "ShotNova", "Falha na captura: " + ex.Message, Forms.ToolTipIcon.Error);
        }
    }

    // ---------- menu de contexto do Windows (Explorer) ----------
    // No Windows 11 essas entradas aparecem em "Mostrar mais opções" (menu clássico).

    private const string ImageVerb = @"Software\Classes\SystemFileAssociations\image\shell\ShotNova";
    private const string DesktopVerb = @"Software\Classes\Directory\Background\shell\ShotNovaCapture";

    private static bool ShellIntegrationInstalled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(ImageVerb);
        return k is not null;
    }

    private void ToggleShellIntegration()
    {
        try
        {
            if (ShellIntegrationInstalled())
            {
                Registry.CurrentUser.DeleteSubKeyTree(ImageVerb, false);
                Registry.CurrentUser.DeleteSubKeyTree(DesktopVerb, false);
                _tray?.ShowBalloonTip(2500, "ShotNova", "Integração removida.", Forms.ToolTipIcon.Info);
                return;
            }

            var exe = Environment.ProcessPath!;
            using (var k = Registry.CurrentUser.CreateSubKey(ImageVerb))
            {
                k.SetValue("", "Editar no ShotNova");
                k.SetValue("Icon", exe);
                using var c = k.CreateSubKey("command");
                c.SetValue("", $"\"{exe}\" \"%1\"");
            }
            using (var k = Registry.CurrentUser.CreateSubKey(DesktopVerb))
            {
                k.SetValue("", "Capturar área com ShotNova");
                k.SetValue("Icon", exe);
                using var c = k.CreateSubKey("command");
                c.SetValue("", $"\"{exe}\" --capture");
            }
            _tray?.ShowBalloonTip(3500, "ShotNova",
                "Integrado. No Windows 11, clique com o botão direito e escolha \"Mostrar mais opções\".",
                Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloonTip(4000, "ShotNova", "Falha na integração: " + ex.Message, Forms.ToolTipIcon.Error);
        }
    }
}
