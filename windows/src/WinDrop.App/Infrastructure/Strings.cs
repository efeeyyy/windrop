using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Windows.Markup;

namespace WinDrop.App.Infrastructure;

/// <summary>Arayüz metinleri: <c>Resources\Strings.resx</c> (Türkçe, nötr) ve <c>Strings.en.resx</c> (İngilizce).</summary>
public static class Strings
{
    private static readonly ResourceManager Manager =
        new("WinDrop.App.Resources.Strings", Assembly.GetExecutingAssembly());

    public static string Get(string key) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? $"!{key}!";

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>Tekil/çoğul: <paramref name="count"/> 1 ise <c>key + ".One"</c>, değilse <paramref name="key"/> kullanılır.</summary>
    public static string Plural(string key, int count, params object[] args) =>
        Format(count == 1 ? key + ".One" : key, args);
}

/// <summary>Servislerin arayüz iş parçacığına olay göndermesi için bağlam; arayüz bağlamı yoksa Dispatcher'a düşer (thread-pool'a asla).</summary>
public static class UiContext
{
    public static SynchronizationContext Capture() =>
        SynchronizationContext.Current
        ?? new System.Windows.Threading.DispatcherSynchronizationContext(
            System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher);
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
