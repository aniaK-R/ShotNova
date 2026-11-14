using System;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace ShotNova;

public sealed class Settings
{
    // Modificadores no formato do Win32: Alt=1, Ctrl=2, Shift=4, Win=8
    public uint HotkeyModifiers { get; set; } = 0;
    public uint HotkeyVk { get; set; } = 0x2C; // Print Screen

    /// <summary>Última pasta onde o usuário salvou uma imagem.</summary>
    public string? LastSaveFolder { get; set; }

    /// <summary>Maior número já usado em nomes "screenshot_N".</summary>
    public int LastNumber { get; set; }

    /// <summary>Pasta inicial do diálogo de salvar: a última usada, se ainda existir.</summary>
    public string SaveFolder()
    {
        if (!string.IsNullOrWhiteSpace(LastSaveFolder) && Directory.Exists(LastSaveFolder)) return LastSaveFolder;
        Directory.CreateDirectory(Paths.Captures);
        return Paths.Captures;
    }

    /// <summary>Próximo nome livre: screenshot_N, N acima do último usado e de qualquer arquivo na pasta.</summary>
    public string NextName(string folder)
    {
        var max = LastNumber;
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "screenshot_*"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(f), @"^screenshot_(\d+)$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > max) max = n;
            }
        }
        catch { /* pasta inacessível: usa só o contador */ }
        return $"screenshot_{max + 1}.png";
    }

    /// <summary>Maior número já usado em nomes "video_N".</summary>
    public int LastVideoNumber { get; set; }

    /// <summary>Próximo nome livre para vídeo: video_N.mp4.</summary>
    public string NextVideoName(string folder)
    {
        var max = LastVideoNumber;
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "video_*"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(f), @"^video_(\d+)$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > max) max = n;
            }
        }
        catch { }
        return $"video_{max + 1}.mp4";
    }

    public void RememberVideo(string filePath)
    {
        var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(filePath), @"^video_(\d+)$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > LastVideoNumber)
        {
            LastVideoNumber = n;
            try { Save(); } catch { }
        }
    }

    /// <summary>Guarda a pasta e, se o nome for screenshot_N, o número usado.</summary>
    public void RememberSave(string filePath)
    {
        LastSaveFolder = Path.GetDirectoryName(filePath);
        var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(filePath), @"^screenshot_(\d+)$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > LastNumber) LastNumber = n;
        try { Save(); } catch { }
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShotNova", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* arquivo corrompido: volta ao padrão */ }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string HotkeyText => Format(HotkeyModifiers, HotkeyVk);

    public static string Format(uint mods, uint vk)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((mods & 2) != 0) parts.Add("Ctrl");
        if ((mods & 1) != 0) parts.Add("Alt");
        if ((mods & 4) != 0) parts.Add("Shift");
        if ((mods & 8) != 0) parts.Add("Win");
        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        parts.Add(key == Key.Snapshot ? "PrintScreen" : key.ToString());
        return string.Join("+", parts);
    }
}
