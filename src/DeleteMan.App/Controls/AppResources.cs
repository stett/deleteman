using Microsoft.UI.Xaml;
using WinRT;

namespace DeleteMan.App.Controls;

/// <summary>
/// Application-level resource lookups from code.
/// </summary>
/// <remarks>
/// A resource dictionary hands back <see cref="object"/>. Under Native AOT a plain C# cast of that object to a
/// projected class (<c>(Style)resources["x"]</c>) throws <see cref="InvalidCastException"/>: CsWinRT has to map the
/// runtime class name to a .NET type to build the right wrapper, and that map is not reliably available for types
/// the app only ever receives. <see cref="CastExtensions.As{T}(object)"/> instead asks the native object for the
/// interface (QueryInterface) and builds the wrapper for <typeparamref name="T"/> directly, which works in both JIT
/// and AOT builds. Every code-side cast of a WinRT object goes through here or <see cref="ThemeLookup"/>.
/// </remarks>
internal static class AppResources
{
    /// <summary>Returns the application resource <paramref name="key"/> as <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException">The key is missing or not a <typeparamref name="T"/>.</exception>
    public static T Get<T>(string key) where T : class
    {
        if (!Application.Current.Resources.TryGetValue(key, out object? value) || value is null)
        {
            throw new InvalidOperationException($"Missing resource '{key}'.");
        }
        return Cast<T>(value) ?? throw new InvalidOperationException($"Resource '{key}' is not a {typeof(T).Name}.");
    }

    /// <summary>
    /// Casts a WinRT object received as <see cref="object"/> to <typeparamref name="T"/>, or returns null when the
    /// object does not implement it. Never throws.
    /// </summary>
    public static T? Cast<T>(object? value) where T : class
    {
        if (value is null)
        {
            return null;
        }
        if (value is T direct)
        {
            return direct;
        }
        if (value is not IWinRTObject)
        {
            return null;
        }
        try
        {
            return value.As<T>();
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }
}
