using Microsoft.UI.Xaml.Media;

namespace DeleteMan.App.Controls;

/// <summary>
/// Runs work at the start of the next frame (UI thread). A storyboard begun late in a long frame starts from
/// that frame's time, so by the next frame it has already skipped as much as the frame took; beginning it here
/// makes the first frame it is seen in its first frame.
/// </summary>
internal static class FrameQueue
{
    private static readonly List<Action> Pending = [];
    private static bool _hooked;

    /// <summary>Queues <paramref name="action"/> for the next <see cref="CompositionTarget.Rendering"/> tick.</summary>
    public static void Next(Action action)
    {
        Pending.Add(action);
        if (!_hooked)
        {
            _hooked = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private static void OnRendering(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _hooked = false;
        Action[] actions = [.. Pending];
        Pending.Clear();
        foreach (Action action in actions)
        {
            action();
        }
    }
}
