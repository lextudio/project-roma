using System;

using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.AssemblyTree;
using ICSharpCode.ILSpy.Docking;
using ICSharpCode.ILSpy.TextView;
using ICSharpCode.ILSpy.ViewModels;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Windows.System;

using SettingsService = ICSharpCode.ILSpy.SettingsService;

namespace Roma.Host.Views;

#if DEBUG
public sealed partial class DebugStepsPane : UserControl
{
    readonly ILAstWritingOptions _writingOptions = new()
    {
        UseFieldSugar = true,
        UseLogicOperationSugar = true
    };
    readonly AssemblyTreeModel _assemblyTreeModel;
    readonly SettingsService _settingsService;
    readonly LanguageService _languageService;
    readonly DockWorkspace _dockWorkspace;
    ILAstLanguage? _language;
    int _lastSelectedStep = int.MaxValue;

    public DebugStepsPane(AssemblyTreeModel assemblyTreeModel, SettingsService settingsService,
        LanguageService languageService, DockWorkspace dockWorkspace)
    {
        _assemblyTreeModel = assemblyTreeModel;
        _settingsService = settingsService;
        _languageService = languageService;
        _dockWorkspace = dockWorkspace;

        InitializeComponent();

        _fieldSugar.IsChecked = true;
        _logicOpSugar.IsChecked = true;

        _writingOptions.PropertyChanged += OnWritingOptionsPropertyChanged;

        if (_languageService.Language is ILAstLanguage l)
        {
            l.StepperUpdated += OnStepperUpdated;
            _language = l;
            OnStepperUpdated(null, EventArgs.Empty);
        }

        _languageService.PropertyChanged += OnLanguageServicePropertyChanged;

        var flyout = new MenuFlyout();
        var showBefore = new MenuFlyoutItem { Text = "Show State Before This Step" };
        showBefore.Click += (_, _) => { var n = GetSelectedNode(); if (n is not null) DecompileAsync(n.BeginStep); };
        flyout.Items.Add(showBefore);
        var showAfter = new MenuFlyoutItem { Text = "Show State After This Step" };
        showAfter.Click += (_, _) => { var n = GetSelectedNode(); if (n is not null) DecompileAsync(n.EndStep); };
        flyout.Items.Add(showAfter);
        var debugStep = new MenuFlyoutItem { Text = "Debug This Step" };
        debugStep.Click += (_, _) => { var n = GetSelectedNode(); if (n is not null) DecompileAsync(n.BeginStep, true); };
        flyout.Items.Add(debugStep);
        _tree.ContextFlyout = flyout;

        _tree.KeyDown += OnTreeKeyDown;
    }

    void OnLanguageServicePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LanguageService.Language))
            return;
        if (_language is not null)
            _language.StepperUpdated -= OnStepperUpdated;
        if (_languageService.Language is ILAstLanguage l)
        {
            l.StepperUpdated += OnStepperUpdated;
            _language = l;
            OnStepperUpdated(null, EventArgs.Empty);
        }
        else
        {
            _language = null;
            _tree.RootNodes.Clear();
        }
    }

    void OnWritingOptionsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => DecompileAsync(_lastSelectedStep));
    }

    void OnCheckBoxChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        switch (cb.Name)
        {
            case "_fieldSugar":
                _writingOptions.UseFieldSugar = cb.IsChecked == true;
                break;
            case "_logicOpSugar":
                _writingOptions.UseLogicOperationSugar = cb.IsChecked == true;
                break;
            case "_showILRanges":
                _writingOptions.ShowILRanges = cb.IsChecked == true;
                break;
            case "_showChildIndex":
                _writingOptions.ShowChildIndexInBlock = cb.IsChecked == true;
                break;
        }
    }

    void OnStepperUpdated(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_language is null) return;
            _tree.RootNodes.Clear();
            foreach (var node in _language.Stepper.Steps)
                _tree.RootNodes.Add(BuildNode(node));
            _lastSelectedStep = int.MaxValue;
        });
    }

    static TreeViewNode BuildNode(Stepper.Node node)
    {
        var tvn = new TreeViewNode { Content = node };
        foreach (var child in node.Children)
            tvn.Children.Add(BuildNode(child));
        return tvn;
    }

    Stepper.Node? GetSelectedNode()
    {
        if (_tree.SelectedNode?.Content is Stepper.Node n)
            return n;
        return null;
    }

    void OnTreeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        var n = GetSelectedNode();
        if (n is null) return;
        if (shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            DecompileAsync(n.BeginStep);
        else
            DecompileAsync(n.EndStep);
        e.Handled = true;
    }

    void DecompileAsync(int step, bool isDebug = false)
    {
        _lastSelectedStep = step;

        if (_dockWorkspace.ActiveTabPage.FrozenContent)
        {
            _dockWorkspace.ActiveTabPage = _dockWorkspace.AddTabPage();
        }

        var state = _dockWorkspace.ActiveTabPage.GetState();
        _dockWorkspace.ActiveTabPage.ShowTextViewAsync(textView => textView.DecompileAsync(
            _assemblyTreeModel.CurrentLanguage,
            _assemblyTreeModel.SelectedNodes,
            null,
            new DecompilationOptions(_assemblyTreeModel.CurrentLanguageVersion,
                _settingsService.DecompilerSettings, _settingsService.DisplaySettings)
            {
                StepLimit = step,
                IsDebug = isDebug,
                TextViewState = state as DecompilerTextViewState
            }));
    }
}
#endif
