using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Windows.Markup;

namespace WinDrop.App.Infrastructure;

/// <summary>Türkçe metinler: <c>Resources\Strings.resx</c>.</summary>
public static class Strings
{
    private static readonly ResourceManager Manager =
        new("WinDrop.App.Resources.Strings", Assembly.GetExecutingAssembly());

    public static string Get(string key) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? $"!{key}!";

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}

/// <summary>XAML: <c>{infra:T Anahtar}</c></summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
