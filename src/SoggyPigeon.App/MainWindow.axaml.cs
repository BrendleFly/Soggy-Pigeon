using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoggyPigeon.App.Services;
using SoggyPigeon.App.ViewModels;
using SoggyPigeon.Protocol;

namespace SoggyPigeon.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly PadService _pad = new();

    public MainWindow()
    {
        AppServices.LoadAll();
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Page)) Dispatcher.UIThread.Post(SyncPage);
        };
        _vm.Match.ForegroundReturned += () => Dispatcher.UIThread.Post(() =>
        {
            WindowState = WindowState.Normal;
            Show();
            Activate();
        });

        // first run: screen name
        if (!AppServices.Data.IsOnboarded)
        {
            NameDialog.IsVisible = true;
            NameBox.Text = AppServices.Data.Name;
            NameBox.Focus();
        }

        SyncPage();

        // fetch the manifest right away so the game list is visible at launch
        _ = _vm.Play.ConnectForManifestAsync();

        // pad users: give the page an initial focus target
        Dispatcher.UIThread.Post(FocusFirst, DispatcherPriority.Background);

        // deep-link for testing/screenshots: --page play|match|history|settings
        var pageArg = Array.FindIndex(Environment.GetCommandLineArgs(), a => a == "--page");
        if (pageArg >= 0 && pageArg + 1 < Environment.GetCommandLineArgs().Length
            && Enum.TryParse<AppPage>(Environment.GetCommandLineArgs()[pageArg + 1], ignoreCase: true, out var p))
            _vm.Page = p;

        _pad.Pressed += b => Dispatcher.UIThread.Post(() => OnPad(b));
        _pad.Start();

        Closed += (_, _) => _pad.Dispose();
    }

    // ---- pages -------------------------------------------------------------

    private readonly Dictionary<AppPage, Control> _viewCache = new();
    private bool _syncingPage;

    private void SyncPage()
    {
        if (_syncingPage) return;   // navigation events can fire mid-navigation
        _syncingPage = true;
        try
        {
            // one view instance per page — VMs are app-lifetime singletons,
            // so rebuilding views per navigation only churns layout/composition
            if (!_viewCache.TryGetValue(_vm.Page, out var view))
            {
                view = _vm.Page switch
                {
                    AppPage.Play => new Views.PlayView { DataContext = _vm.Play },
                    AppPage.Games => new Views.GamesView { DataContext = _vm.Play },
                    AppPage.History => new Views.HistoryView { DataContext = _vm.HistoryVm },
                    AppPage.Settings => new Views.SettingsView { DataContext = _vm.Settings },
                    _ => null,
                };
                if (view is not null) _viewCache[_vm.Page] = view;
            }
            if (view is not null) PageHost.Content = view;
            // give the pad a landing spot on every page
            Dispatcher.UIThread.Post(FocusFirst, DispatcherPriority.Background);

            SetActive(NavPlay, AppPage.Play);
            SetActive(NavGames, AppPage.Games);
            SetActive(NavHistory, AppPage.History);
            SetActive(NavSettings, AppPage.Settings);
        }
        finally { _syncingPage = false; }
    }

    private void SetActive(Button button, AppPage page)
    {
        if (_vm.Page == page) button.Classes.Add("active");
        else button.Classes.Remove("active");
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
            _vm.Page = Enum.Parse<AppPage>(tag);
    }

    // ---- first-run name ------------------------------------------------------

    private void OnNameOk(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length < 3)
        {
            NameDialogError.Text = "3 to 15 characters, please.";
            NameDialogError.IsVisible = true;
            return;
        }
        if (NameFilter.IsOffensive(name))
        {
            NameDialogError.Text = "not allowed: pick a different name";
            NameDialogError.IsVisible = true;
            return;
        }
        NameDialogError.IsVisible = false;
        AppServices.Data.Name = name;
        AppServices.Data.Save();
        NameDialog.IsVisible = false;
        _vm.RaiseOnboarded();
    }

    // ---- gamepad → UI (§6.2) --------------------------------------------------

    // ---- gamepad: explicit spatial focus navigation -----------------------
    // (synthetic arrow keys only work when something already has focus; here
    // the first D-pad press focuses the page, and A presses the focused control)

    private void OnPad(PadButton b)
    {
        // While a session is live the pad belongs to the game, not to us:
        // SDL joystick events are global (not focus-scoped), so without this
        // gate a background app navigates itself while the player is playing.
        if (_vm.Match.IsBusy)
        {
            PadService.Log($"button {b} ignored (session live)");
            return;
        }
        PadService.Log($"button {b}, focus={DescribeFocus()}");

        if (b is PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right)
        {
            MoveFocus(b switch
            {
                PadButton.Up => Dir.Up,
                PadButton.Down => Dir.Down,
                PadButton.Left => Dir.Left,
                _ => Dir.Right,
            });
            return;
        }

        var focused = FocusManager?.GetFocusedElement() as Control;
        if (focused is null || !focused.IsEffectivelyVisible || !focused.IsEffectivelyEnabled)
        {
            FocusFirst();
            return;
        }

        var key = b switch
        {
            PadButton.A or PadButton.Start => Key.Enter,
            PadButton.B => Key.Escape,
            _ => (Key?)null,
        };
        if (key is null) return;
        var ev = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key.Value,
            Source = focused,
        };
        focused.RaiseEvent(ev);
    }

    private enum Dir { Up, Down, Left, Right }

    /// <summary>All focusable, enabled, visible controls in the window — the
    /// nav bar and the page, so the pad can always reach the page tabs.</summary>
    private List<(Control Control, Rect Rect)> FocusCandidates()
    {
        // while the first-run dialog is up, focus must stay inside it
        Visual root = NameDialog.IsVisible ? NameDialog : this;
        var list = new List<(Control, Rect)>();
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            if (!control.Focusable || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled) continue;
            var topLeft = control.TranslatePoint(new Point(0, 0), this);
            if (topLeft is not { } p) continue;
            var bounds = control.Bounds;
            list.Add((control, new Rect(p, new Size(bounds.Width, bounds.Height))));
        }
        return list;
    }

    private void FocusFirst()
    {
        var first = FocusCandidates().FirstOrDefault();
        if (first.Control is not null) { first.Control.Focus(NavigationMethod.Directional); PadService.Log($"focused first {first.Control.GetType().Name}"); }
    }

    private void MoveFocus(Dir dir)
    {
        var candidates = FocusCandidates();
        if (candidates.Count == 0) return;
        var current = FocusManager?.GetFocusedElement() as Control;
        if (current is null || !candidates.Any(c => c.Control == current))
        {
            FocusFirst();
            return;
        }
        var cr = candidates.First(c => c.Control == current).Rect;
        Control? best = null;
        var bestScore = double.MaxValue;
        foreach (var (control, r) in candidates)
        {
            if (control == current) continue;
            double primary, secondary;
            switch (dir)
            {
                case Dir.Up:
                    if (r.Bottom > cr.Top) continue;        // must lie above
                    primary = cr.Top - r.Bottom;
                    secondary = Gap(cr.Left, cr.Right, r.Left, r.Right);
                    break;
                case Dir.Down:
                    if (r.Top < cr.Bottom) continue;        // must lie below
                    primary = r.Top - cr.Bottom;
                    secondary = Gap(cr.Left, cr.Right, r.Left, r.Right);
                    break;
                case Dir.Left:
                    if (r.Right > cr.Left) continue;
                    primary = cr.Left - r.Right;
                    secondary = Gap(cr.Top, cr.Bottom, r.Top, r.Bottom);
                    break;
                default:
                    if (r.Left < cr.Right) continue;
                    primary = r.Left - cr.Right;
                    secondary = Gap(cr.Top, cr.Bottom, r.Top, r.Bottom);
                    break;
            }
            // edge-gap scoring: an element whose span overlaps yours (secondary 0)
            // wins over one merely closer in a straight line
            var score = primary + secondary * 2;
            if (score < bestScore) { bestScore = score; best = control; }
        }
        if (best is not null) { best.Focus(NavigationMethod.Directional); PadService.Log($"focus -> {best.GetType().Name}"); }
    }

    /// <summary>Distance between two 1-D intervals; 0 when they overlap.</summary>
    private static double Gap(double a1, double a2, double b1, double b2)
        => b1 >= a2 ? b1 - a2 : (a1 >= b2 ? a1 - b2 : 0);

    private string DescribeFocus()
    {
        var f = FocusManager?.GetFocusedElement() as Control;
        return f is null ? "none" : f.GetType().Name;
    }
}
