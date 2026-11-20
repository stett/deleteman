using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace DeleteMan.App.Controls;

/// <summary>
/// Resolves theme resources for an element from code. <c>Application.Current.Resources[key]</c> answers for the
/// application theme only, so an element under a <c>RequestedTheme</c> override would get the wrong colours. This
/// helper picks the theme dictionary that matches the element's <see cref="FrameworkElement.ActualTheme"/> instead.
/// </summary>
/// <remarks>
/// Every app-defined theme value in Styles\Colors.xaml is a <see cref="SolidColorBrush"/>, even the keys that end in
/// "Color": a colour read from code would come back as a boxed struct, and unboxing that is exactly the kind of
/// runtime type lookup Native AOT cannot do (see <see cref="AppResources"/>). Brushes are classes and cast safely.
/// </remarks>
internal static class ThemeLookup
{
    private static readonly AccessibilitySettings Accessibility = new();

    /// <summary>Finds <paramref name="key"/> for the element's current theme, or null when it is not defined.</summary>
    public static object? Find(FrameworkElement element, string key)
    {
        string theme = Accessibility.HighContrast
            ? "HighContrast"
            : element.ActualTheme == ElementTheme.Light ? "Light" : "Default";
        ResourceDictionary resources = Application.Current.Resources;
        return FindIn(resources, theme, key)
            ?? (resources.TryGetValue(key, out object? value) ? value : null);
    }

    /// <summary>Resolves a colour resource (stored as a solid brush), or <paramref name="fallback"/>.</summary>
    public static Color Color(FrameworkElement element, string key, Color fallback)
    {
        if (Brush(element, key) is SolidColorBrush solid)
        {
            return solid.Color;
        }
        return fallback;
    }

    /// <summary>Resolves a brush resource, or null (with a trace) when it is missing or not a brush.</summary>
    public static Brush? Brush(FrameworkElement element, string key)
    {
        object? found = Find(element, key);
        if (found is null)
        {
            Trace.TraceWarning($"theme resource '{key}' is not defined");
            return null;
        }
        Brush? brush = AppResources.Cast<SolidColorBrush>(found) ?? AppResources.Cast<Brush>(found);
        if (brush is null)
        {
            Trace.TraceWarning($"theme resource '{key}' is not a brush");
        }
        return brush;
    }

    // Depth-first over merged dictionaries so the app's own Colors.xaml is found wherever it is merged.
    private static object? FindIn(ResourceDictionary dictionary, string theme, string key)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(theme, out object? themed)
            && AppResources.Cast<ResourceDictionary>(themed) is { } themeDictionary
            && themeDictionary.TryGetValue(key, out object? found))
        {
            return found;
        }
        foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
        {
            if (FindIn(merged, theme, key) is { } hit)
            {
                return hit;
            }
        }
        return null;
    }
}
