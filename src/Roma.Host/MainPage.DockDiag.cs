#if DEBUG
// TEMPORARY DevFlow diagnostics for verifying dock-layout persistence. Remove after verification.
using System.Linq;
using AvalonDock.Layout;
using LeXtudio.DevFlow.Agent.Core;

namespace Roma.Host;

public sealed partial class MainPage
{
    [DevFlowAction("roma.dock-state", Description = "Reports tool-pane visibility + persisted layout state.")]
    public static string DockState()
    {
        var page = _current;
        if (page is null) return "MainPage not available.";
        string result = string.Empty;
        using var done = new System.Threading.ManualResetEventSlim();
        page.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var layout = page.DockManager.Layout;
                var ancs = (layout?.Descendents().OfType<LayoutAnchorable>() ?? Enumerable.Empty<LayoutAnchorable>())
                    .Concat(layout?.Hidden ?? Enumerable.Empty<LayoutAnchorable>());
                var lines = ancs.Select(a => $"{a.ContentId}: visible={a.IsVisible} hidden={a.IsHidden}");
                var dl = page._assemblyContext.SettingsService.SessionSettings.DockLayout;
                result = string.Join("\n", lines) + $"\nDockLayout.Valid={dl?.Valid}";
            }
            catch (System.Exception ex) { result = ex.ToString(); }
            finally { done.Set(); }
        });
        done.Wait(5000);
        return result;
    }

    [DevFlowAction("roma.hide-search", Description = "Hides the Search tool pane.")]
    public static string HideSearch()
    {
        var page = _current;
        if (page is null) return "MainPage not available.";
        string result = string.Empty;
        using var done = new System.Threading.ManualResetEventSlim();
        page.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var search = page.DockWorkspace.ToolPanes.FirstOrDefault(p => p.ContentId == "searchPane");
                if (search is not null) search.IsVisible = false;
                result = $"hidden; searchVisible={search?.IsVisible}";
            }
            catch (System.Exception ex) { result = ex.ToString(); }
            finally { done.Set(); }
        });
        done.Wait(5000);
        return result;
    }

    [DevFlowAction("roma.persist-layout", Description = "Serializes the current layout to settings (as on exit).")]
    public static string PersistLayout()
    {
        var page = _current;
        if (page is null) return "MainPage not available.";
        string result = string.Empty;
        using var done = new System.Threading.ManualResetEventSlim();
        page.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                PersistDockLayoutOnExit();
                var dl = page._assemblyContext.SettingsService.SessionSettings.DockLayout;
                result = $"persisted; live DockLayout.Valid={dl?.Valid}";
            }
            catch (System.Exception ex) { result = ex.ToString(); }
            finally { done.Set(); }
        });
        done.Wait(5000);
        return result;
    }
}
#endif
