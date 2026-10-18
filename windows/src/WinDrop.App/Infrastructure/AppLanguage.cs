using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using WinDrop.Platform;

namespace WinDrop.App.Infrastructure;

/// <summary>
/// Arayüz dili: <c>system</c> (Windows dili Türkçe ise Türkçe, değilse İngilizce), <c>tr</c>, <c>en</c>.
/// Sayı/boyut/tarih biçimleri seçili kültüre göre; "system"de Windows bölge ayarı korunur.
/// </summary>
public static class AppLanguage
{
    public const string Auto = "system";
    public const string Turkish = "tr";
    public const string English = "en";

    public static string Normalize(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        Turkish => Turkish,
        English => English,
        _ => Auto,
    };

    /// <summary>Çözümlenmiş arayüz dili: <c>tr</c> ya da <c>en</c>.</summary>
    public static string Resolve(string? language, CultureInfo? systemUi = null) => Normalize(language) switch
    {
        Turkish => Turkish,
        English => English,
        _ => (systemUi ?? CultureInfo.InstalledUICulture).TwoLetterISOLanguageName == "tr" ? Turkish : English,
    };

    /// <summary>Ayara göre iş parçacığı ve varsayılan kültürleri ayarlar (pencereler açılmadan önce çağrılır).</summary>
    public static void Apply(string? language)
    {
        var lang = Normalize(language);
        var ui = CultureInfo.GetCultureInfo(Resolve(lang) == Turkish ? "tr-TR" : "en-US");
        CultureInfo.DefaultThreadCurrentUICulture = ui;
        CultureInfo.CurrentUICulture = ui;
        if (lang != Auto)
        {
            CultureInfo.DefaultThreadCurrentCulture = ui;
            CultureInfo.CurrentCulture = ui;
        }
    }

    /// <summary>WPF bağlamalarının sayı/tarih biçimi için varsayılan dili (yoksa en-US) geçerli kültüre çeker.</summary>
    public static void ApplyToWpf() =>
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));
}

/// <summary>Platform'dan gelen <see cref="UserError"/> kodlarını seçili arayüz diline çevirir (<c>Err.&lt;Kod&gt;</c>).</summary>
public static class ErrorTexts
{
    public static string Format(UserError error) => string.Join(" ", error.Parts.Select(Format));

    public static string Format(UserErrorPart part) =>
        part.Code == UserErrorCode.Raw
            ? part.Arg ?? ""
            : Strings.Format("Err." + part.Code, part.Arg ?? "", part.Arg2 ?? "");
}
