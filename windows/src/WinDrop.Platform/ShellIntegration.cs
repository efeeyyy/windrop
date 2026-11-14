using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinDrop.Platform;

/// <summary>
/// SendTo kısayolu ve otomatik başlangıç (yönetici gerektirmez). Yollar enjekte edilebilir; varsayılanlar gerçek
/// kullanıcı konumlarıdır (<c>%APPDATA%\Microsoft\Windows\SendTo\WinDrop.lnk</c>, <c>HKCU\...\Run</c>).
/// </summary>
public sealed class ShellIntegration
{
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "WinDrop";

    private readonly string _exePath;
    private readonly string _shortcutPath;
    private readonly string _runKeyPath;

    public ShellIntegration(string exePath, string? shortcutPath = null, string? runKeyPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        _exePath = Path.GetFullPath(exePath);
        _shortcutPath = shortcutPath ?? DefaultShortcutPath();
        _runKeyPath = runKeyPath ?? DefaultRunKeyPath;
    }

    public static string DefaultShortcutPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "SendTo", "WinDrop.lnk");

    public string ShortcutPath => _shortcutPath;

    // ---- SendTo ----

    public void CreateSendToShortcut()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_shortcutPath)!);
        WriteShortcut(_shortcutPath, _exePath, "--send");
    }

    public bool RemoveSendToShortcut()
    {
        if (!File.Exists(_shortcutPath)) return false;
        File.Delete(_shortcutPath);
        return true;
    }

    public bool IsSendToInstalled => File.Exists(_shortcutPath);

    /// <summary>Kısayolun hedef ve argümanını okur (yoksa null).</summary>
    public static (string Target, string Arguments)? ReadShortcut(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return null;
        var shell = CreateWScriptShell();
        try
        {
            dynamic sc = shell.CreateShortcut(lnkPath);
            try { return ((string)sc.TargetPath, (string)sc.Arguments); }
            finally { Marshal.FinalReleaseComObject(sc); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static void WriteShortcut(string lnkPath, string target, string arguments)
    {
        dynamic shell = CreateWScriptShell();
        try
        {
            dynamic sc = shell.CreateShortcut(lnkPath);
            try
            {
                sc.TargetPath = target;
                sc.Arguments = arguments;
                sc.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
                sc.Description = "WinDrop ile gönder";
                sc.IconLocation = target + ",0";
                sc.Save();
            }
            finally { Marshal.FinalReleaseComObject(sc); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static dynamic CreateWScriptShell()
    {
        var type = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new PlatformNotSupportedException("WScript.Shell kullanılamıyor.");
        return Activator.CreateInstance(type)!;
    }

    // ---- Otomatik başlangıç ----

    public string AutoStartCommand => $"\"{_exePath}\" --hidden";

    public void EnableAutoStart()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true);
        key.SetValue(RunValueName, AutoStartCommand, RegistryValueKind.String);
    }

    public bool DisableAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
        if (key?.GetValue(RunValueName) == null) return false;
        key.DeleteValue(RunValueName);
        return true;
    }

    public bool IsAutoStartEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
            return key?.GetValue(RunValueName) is string s && string.Equals(s, AutoStartCommand, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Ayarlardaki istenen durumu uygular.</summary>
    public void Apply(AppSettings settings)
    {
        if (settings.SendToEnabled) CreateSendToShortcut(); else RemoveSendToShortcut();
        if (settings.AutoStartEnabled) EnableAutoStart(); else DisableAutoStart();
    }
}
