using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    private ScreenRecorder? _rec;
    private RecorderUi? _recUi;
    private string? _lastVideo;
    private Forms.ToolStripItem? _stopItem;
    private readonly System.Collections.Generic.List<Forms.ToolStripItem> _recItems = new();

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
        if (_tray is not null)
            _tray.Text = _rec is not null
                ? "ShotNova ● gravando (" + _settings.HotkeyText + " para parar)"
                : "ShotNova (" + _settings.HotkeyText + ")";
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
        _recItems.Add(menu.Items.Add("Gravar tela inteira", null, (_, _) => Dispatcher.BeginInvoke(new Action(RecordScreen))));
        _recItems.Add(menu.Items.Add("Gravar janela…", null, (_, _) => Dispatcher.BeginInvoke(new Action(RecordWindow))));
        _recItems.Add(menu.Items.Add("Gravar área…", null, (_, _) => Dispatcher.BeginInvoke(new Action(RecordArea))));
        _stopItem = menu.Items.Add("■ Parar gravação", null, (_, _) => Dispatcher.BeginInvoke(new Action(StopRecording)));
        _stopItem.Visible = false;

        var audioMenu = new Forms.ToolStripMenuItem("Áudio da gravação");
        var sysItem = new Forms.ToolStripMenuItem("Som do computador") { CheckOnClick = true };
        var micItem = new Forms.ToolStripMenuItem("Microfone") { CheckOnClick = true };
        sysItem.Click += (_, _) => { var s = Settings.Load(); s.RecordSystemAudio = sysItem.Checked; s.Save(); };
        micItem.Click += (_, _) => { var s = Settings.Load(); s.RecordMic = micItem.Checked; s.Save(); };
        audioMenu.DropDownItems.Add(sysItem);
        audioMenu.DropDownItems.Add(micItem);
        menu.Items.Add(audioMenu);
        _recItems.Add(audioMenu);
        menu.Opening += (_, _) =>
        {
            var s = Settings.Load();
            sysItem.Checked = s.RecordSystemAudio;
            micItem.Checked = s.RecordMic;
        };
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Abrir pasta de capturas", null, (_, _) => OpenFolder(Paths.Captures));
        menu.Items.Add("Abrir pasta do histórico", null, (_, _) => OpenFolder(Paths.History));
        menu.Items.Add("Abrir pasta de vídeos", null, (_, _) => OpenFolder(Paths.Videos));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Configurações (atalho)…", null, (_, _) => Dispatcher.BeginInvoke(new Action(OpenSettings)));
        var shellItem = new Forms.ToolStripMenuItem("Integrar ao menu de contexto do Windows") { CheckOnClick = false };
        shellItem.Click += (_, _) => ToggleShellIntegration();
        menu.Items.Add(shellItem);
        menu.Opening += (_, _) =>
        {
            shellItem.Checked = ShellIntegrationInstalled();
            var recording = _rec is not null;
            if (_stopItem is not null) _stopItem.Visible = recording;
            foreach (var i in _recItems) i.Enabled = !recording;
        };
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Dispatcher.BeginInvoke(new Action(Shutdown)));

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "ShotNova",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.BalloonTipClicked += (_, _) =>
        {
            var v = _lastVideo;
            _lastVideo = null;
            if (v is not null && File.Exists(v))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{v}\"") { UseShellExecute = true });
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

    private void StartCapture() => StartCaptureCore(false);

    private void StartCaptureCore(bool recordFirst)
    {
        // o atalho de captura também PÁRA uma gravação em andamento
        if (_rec is not null) { StopRecording(); return; }
        if (_capturing) return;
        _capturing = true;
        try
        {
            var shot = ScreenGrabber.GrabVirtualScreen(out var vs);
            var win = new CaptureWindow(shot, vs, recordFirst);
            win.Info += msg => _tray?.ShowBalloonTip(1800, "ShotNova", msg, Forms.ToolTipIcon.Info);
            win.RecordRequested += async rect =>
            {
                await Task.Delay(500); // deixa o overlay sumir antes de começar
                BeginRecording(() => rect, rect);
            };
            win.Closed += (_, _) => _capturing = false;
            win.Show();
        }
        catch (Exception ex)
        {
            _capturing = false;
            _tray?.ShowBalloonTip(4000, "ShotNova", "Falha na captura: " + ex.Message, Forms.ToolTipIcon.Error);
        }
    }

    // ---------- gravação de vídeo ----------

    private void RecordArea() => StartCaptureCore(true);

    private async void RecordScreen()
    {
        if (_rec is not null || _capturing) return;
        var b = Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds; // monitor onde está o cursor
        await Task.Delay(500);
        BeginRecording(() => b, b);
    }

    private void RecordWindow()
    {
        if (_rec is not null || _capturing) return;
        _capturing = true;
        var picker = new WindowPicker(Forms.SystemInformation.VirtualScreen);
        picker.Closed += (_, _) => _capturing = false;
        picker.Picked += async (hwnd, bounds) =>
        {
            await Task.Delay(500);
            var last = bounds;
            // acompanha a janela se ela for movida durante a gravação
            BeginRecording(() =>
            {
                if (Win.IsWindow(hwnd) && !Win.IsIconic(hwnd) && Win.TryFrameBounds(hwnd, out var r)) last = r;
                return last;
            }, bounds);
        };
        picker.Show();
    }

    private void BeginRecording(Func<Drawing.Rectangle> region, Drawing.Rectangle initial)
    {
        if (_rec is not null) return;

        var ffmpeg = ScreenRecorder.FindFfmpeg();
        if (ffmpeg is null) { OfferFfmpegInstall(); return; }

        // H.264 em yuv420p exige largura e altura pares
        var w = initial.Width & ~1;
        var h = initial.Height & ~1;
        if (w < 16 || h < 16)
        {
            _tray?.ShowBalloonTip(3000, "ShotNova", "A área é pequena demais para gravar.", Forms.ToolTipIcon.Warning);
            return;
        }

        var st = Settings.Load();
        Directory.CreateDirectory(Paths.Videos);
        var path = Path.Combine(Paths.Videos, st.NextVideoName(Paths.Videos));

        AudioMixer? audio = null;
        if (st.RecordSystemAudio || st.RecordMic)
        {
            audio = new AudioMixer(st.RecordSystemAudio, st.RecordMic);
            if (audio.Warning is not null)
                _tray?.ShowBalloonTip(4000, "ShotNova", audio.Warning.Trim(), Forms.ToolTipIcon.Warning);
            if (!audio.HasSources) { audio.Dispose(); audio = null; } // grava só o vídeo
        }

        var rec = new ScreenRecorder(region, new Drawing.Size(w, h), path, audio);
        try
        {
            rec.Start(ffmpeg);
        }
        catch (Exception ex)
        {
            audio?.Dispose();
            _tray?.ShowBalloonTip(4000, "ShotNova", "Não foi possível iniciar a gravação: " + ex.Message, Forms.ToolTipIcon.Error);
            return;
        }

        _rec = rec;
        _recUi = new RecorderUi(region);
        _recUi.StopRequested += StopRecording;
        UpdateTrayText();
    }

    private async void StopRecording()
    {
        var rec = _rec;
        if (rec is null) return;
        _rec = null;
        _recUi?.Dispose();
        _recUi = null;
        UpdateTrayText();
        _tray?.ShowBalloonTip(1500, "ShotNova", "Finalizando o vídeo…", Forms.ToolTipIcon.Info);

        var error = await Task.Run(rec.Stop);
        if (error is null)
        {
            Settings.Load().RememberVideo(rec.Output);
            _lastVideo = rec.Output;
            _tray?.ShowBalloonTip(4000, "ShotNova", $"Vídeo salvo: {Path.GetFileName(rec.Output)} (clique para abrir a pasta)", Forms.ToolTipIcon.Info);
        }
        else
        {
            MessageBox.Show("A gravação falhou:\n\n" + error, "ShotNova", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OfferFfmpegInstall()
    {
        var answer = MessageBox.Show(
            "Para gravar vídeo, o ShotNova usa o ffmpeg, que não foi encontrado neste computador.\n\n" +
            "Instalar agora pelo winget (Gyan.FFmpeg)? Uma janela de terminal vai mostrar o andamento.\n\n" +
            "Depois que terminar, é só gravar de novo. Também dá para colocar o ffmpeg.exe ao lado do ShotNova.exe.",
            "ShotNova - ffmpeg necessário", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            Process.Start(new ProcessStartInfo("winget",
                "install --id Gyan.FFmpeg --exact --accept-package-agreements --accept-source-agreements")
            { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Não foi possível chamar o winget: " + ex.Message, "ShotNova");
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
