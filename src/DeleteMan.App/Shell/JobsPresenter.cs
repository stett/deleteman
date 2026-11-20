using DeleteMan.App.Controls;
using DeleteMan.Engine;
using DeleteMan.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeleteMan.App.Shell;

/// <summary>Renders the jobs banner (port of render.ts <c>renderJobs</c>): one <see cref="JobLine"/> per job, keyed by id.</summary>
internal sealed class JobsPresenter
{
    private readonly FrameworkElement _banner;
    private readonly Panel _lines;
    private readonly Dictionary<string, (JobLine Line, JobLineView View)> _byId = [];

    /// <summary>Fills <paramref name="lines"/> and hides <paramref name="banner"/> while there are no jobs.</summary>
    public JobsPresenter(FrameworkElement banner, Panel lines)
    {
        _banner = banner;
        _lines = lines;
    }

    /// <summary>Details was clicked (job id).</summary>
    public event Action<string>? DetailsInvoked;

    /// <summary>Cancel was clicked (job id).</summary>
    public event Action<string>? CancelInvoked;

    /// <summary>The ✕ dismiss button was clicked (job id).</summary>
    public event Action<string>? DismissInvoked;

    /// <summary>Reconciles the banner with <paramref name="views"/>.</summary>
    public void Render(IReadOnlyList<JobLineView> views)
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < views.Count; i++)
        {
            JobLineView v = views[i];
            seen.Add(v.Id);
            if (!_byId.TryGetValue(v.Id, out var entry))
            {
                var line = new JobLine { AutomationKey = $"job:{v.Id}" };
                string id = v.Id;
                line.DetailsClicked += (_, _) => DetailsInvoked?.Invoke(id);
                line.CancelClicked += (_, _) => CancelInvoked?.Invoke(id);
                line.DismissClicked += (_, _) => DismissInvoked?.Invoke(id);
                entry = (line, v);
                Apply(line, v);
            }
            else if (!entry.View.Equals(v))
            {
                Apply(entry.Line, v);
                entry.View = v;
            }
            _byId[v.Id] = entry;

            int current = _lines.Children.IndexOf(entry.Line);
            if (current < 0)
            {
                _lines.Children.Insert(Math.Min(i, _lines.Children.Count), entry.Line);
            }
            else if (current != i)
            {
                _lines.Children.Move((uint)current, (uint)i);
            }
        }
        foreach ((string id, var entry) in _byId.ToList())
        {
            if (!seen.Contains(id))
            {
                _byId.Remove(id);
                _lines.Children.Remove(entry.Line);
            }
        }
        _banner.Visibility = views.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void Apply(JobLine line, JobLineView v)
    {
        line.Glyph = v.Status switch
        {
            JobStatus.Queued => JobGlyph.Queued,
            JobStatus.Running => JobGlyph.Running,
            JobStatus.Done => JobGlyph.Done,
            JobStatus.DoneWithErrors => JobGlyph.DoneWithErrors,
            JobStatus.Cancelled => JobGlyph.Cancelled,
            _ => JobGlyph.Failed,
        };
        line.Path = v.Path;
        line.StatusText = v.Text;
        line.IsResumed = v.Resumed;
        line.HasErrors = v.HasErrors;
        line.IsActive = v.Active;
    }
}
