using System;
using System.Windows;
using System.Windows.Threading;

namespace WindowAnchor;

/// <summary>
/// A MenuItem that gives the pointer a short grace period when moving from a
/// submenu header into its popup. This is useful for tray menus because the
/// submenu is hosted in a separate Popup window and may flip to the opposite
/// side of the parent near a screen edge.
/// </summary>
public sealed class DelayedSubmenuMenuItem : System.Windows.Controls.MenuItem
{
    public static readonly DependencyProperty SubmenuCloseDelayProperty =
        DependencyProperty.Register(
            nameof(SubmenuCloseDelay),
            typeof(int),
            typeof(DelayedSubmenuMenuItem),
            new FrameworkPropertyMetadata(250, null, CoerceSubmenuCloseDelay));

    private DispatcherTimer? _submenuCloseTimer;
    private System.Windows.Controls.Primitives.Popup? _submenuPopup;
    private FrameworkElement? _submenuSurface;

    /// <summary>
    /// Time in milliseconds that the submenu remains open after the pointer
    /// leaves the header. Entering the submenu cancels the pending close.
    /// Set to 0 to use normal WPF MenuItem behaviour.
    /// </summary>
    public int SubmenuCloseDelay
    {
        get => (int)GetValue(SubmenuCloseDelayProperty);
        set => SetValue(SubmenuCloseDelayProperty, value);
    }

    public override void OnApplyTemplate()
    {
        DetachPopupHandlers();

        base.OnApplyTemplate();

        _submenuPopup =
            GetTemplateChild("PART_Popup") as System.Windows.Controls.Primitives.Popup;

        if (_submenuPopup != null)
        {
            _submenuPopup.Opened += OnSubmenuPopupOpened;
            _submenuPopup.Closed += OnSubmenuPopupClosed;
            AttachSubmenuSurface(_submenuPopup.Child as FrameworkElement);
        }
    }

    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        CancelPendingSubmenuClose();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        // Calling MenuItem.OnMouseLeave immediately lets WPF deselect/close the
        // hierarchy before the pointer can cross a tiny popup boundary. Suppress
        // that immediate close while the submenu is open and close it ourselves
        // after the configured grace period instead.
        if (IsSubmenuOpen && SubmenuCloseDelay > 0)
        {
            StartSubmenuCloseTimer();
            return;
        }

        base.OnMouseLeave(e);
    }

    private static object CoerceSubmenuCloseDelay(DependencyObject d, object baseValue)
    {
        return Math.Max(0, (int)baseValue);
    }

    private void OnSubmenuPopupOpened(object? sender, EventArgs e)
    {
        AttachSubmenuSurface(_submenuPopup?.Child as FrameworkElement);
        CancelPendingSubmenuClose();
    }

    private void OnSubmenuPopupClosed(object? sender, EventArgs e)
    {
        CancelPendingSubmenuClose();
    }

    private void OnSubmenuSurfaceMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        CancelPendingSubmenuClose();
    }

    private void OnSubmenuSurfaceMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (IsSubmenuOpen && !IsMouseOver)
            StartSubmenuCloseTimer();
    }

    private void StartSubmenuCloseTimer()
    {
        if (SubmenuCloseDelay <= 0)
        {
            CloseSubmenuAfterGracePeriod();
            return;
        }

        _submenuCloseTimer ??= new DispatcherTimer(DispatcherPriority.Input);
        _submenuCloseTimer.Tick -= OnSubmenuCloseTimerTick;
        _submenuCloseTimer.Tick += OnSubmenuCloseTimerTick;
        _submenuCloseTimer.Interval = TimeSpan.FromMilliseconds(SubmenuCloseDelay);
        _submenuCloseTimer.Stop();
        _submenuCloseTimer.Start();
    }

    private void CancelPendingSubmenuClose()
    {
        _submenuCloseTimer?.Stop();
    }

    private void OnSubmenuCloseTimerTick(object? sender, EventArgs e)
    {
        _submenuCloseTimer?.Stop();

        // The pointer successfully crossed into either surface: keep it open.
        if (IsMouseOver || _submenuSurface?.IsMouseOver == true)
            return;

        CloseSubmenuAfterGracePeriod();
    }

    private void CloseSubmenuAfterGracePeriod()
    {
        if (!IsSubmenuOpen)
            return;

        IsSubmenuOpen = false;

        // OnMouseLeave was intentionally deferred, so clear the visual highlight
        // when the grace period expires outside both menu surfaces.
        IsHighlighted = false;
    }

    private void AttachSubmenuSurface(FrameworkElement? surface)
    {
        if (ReferenceEquals(_submenuSurface, surface))
            return;

        if (_submenuSurface != null)
        {
            _submenuSurface.MouseEnter -= OnSubmenuSurfaceMouseEnter;
            _submenuSurface.MouseLeave -= OnSubmenuSurfaceMouseLeave;
        }

        _submenuSurface = surface;

        if (_submenuSurface != null)
        {
            _submenuSurface.MouseEnter += OnSubmenuSurfaceMouseEnter;
            _submenuSurface.MouseLeave += OnSubmenuSurfaceMouseLeave;
        }
    }

    private void DetachPopupHandlers()
    {
        CancelPendingSubmenuClose();

        if (_submenuPopup != null)
        {
            _submenuPopup.Opened -= OnSubmenuPopupOpened;
            _submenuPopup.Closed -= OnSubmenuPopupClosed;
        }

        AttachSubmenuSurface(null);
        _submenuPopup = null;
    }
}
