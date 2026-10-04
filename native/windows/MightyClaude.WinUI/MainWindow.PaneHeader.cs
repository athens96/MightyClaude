using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        private Grid? paneHeader;
        private StackPanel? paneHeaderState;
        private Button? paneHeaderCopy;
        private bool paneHeaderLayoutQueued;

        private void InitializeResponsiveHeader(Grid header, StackPanel state, Button copy)
        {
            paneHeader = header; paneHeaderState = state; paneHeaderCopy = copy;
            header.RowDefinitions.Add(new() { Height = GridLength.Auto });
            header.RowDefinitions.Add(new() { Height = GridLength.Auto });
            header.Loaded += (_, _) => QueuePaneHeaderLayout();
            header.SizeChanged += (_, _) => QueuePaneHeaderLayout();
            state.SizeChanged += (_, _) => QueuePaneHeaderLayout();
            elapsed.SizeChanged += (_, _) => QueuePaneHeaderLayout();
            statusLineToggle.SizeChanged += (_, _) => QueuePaneHeaderLayout();
        }

        private void QueuePaneHeaderLayout()
        {
            if (paneHeaderLayoutQueued || !QueuePaneAlive) return;
            paneHeaderLayoutQueued = true;
            if (!Container.DispatcherQueue.TryEnqueue(() =>
            {
                paneHeaderLayoutQueued = false;
                if (!QueuePaneAlive || paneHeader is not { IsLoaded: true, ActualWidth: > 0 } header || paneHeaderState is not { } state || paneHeaderCopy is not { } copy) return;
                var narrow = header.ActualWidth < 420;
                // The star column may initially arrange the switch at zero if
                // a long status took its width. Reserve its natural size before
                // trimming the status; don't perpetuate that clipped width.
                modeSwitch?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var modesWidth = modeSwitch?.DesiredSize.Width ?? 0;
                // A second row gives the mode controls their own measured space;
                // the status, Copy action and native control instances stay put.
                header.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
                Grid.SetColumnSpan(state, narrow ? 2 : 1);
                if (modeSwitch is not null)
                {
                    Grid.SetRow(modeSwitch, narrow ? 1 : 0); Grid.SetColumn(modeSwitch, narrow ? 0 : 1);
                    Grid.SetColumnSpan(modeSwitch, narrow ? 3 : 1);
                    modeSwitch.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                    header.RowSpacing = narrow ? 4 : 0;
                }
                // Long localized status text trims before pushing the Copy
                // action outside the pane. Other status icons retain their size.
                var other = state.Children.OfType<FrameworkElement>().Where(child => !ReferenceEquals(child, label) && child.Visibility == Visibility.Visible).ToArray();
                var available = Math.Max(0, header.ActualWidth - (copy.Visibility == Visibility.Visible ? copy.ActualWidth + header.ColumnSpacing : 0));
                if (!narrow && modeSwitch is not null) available -= modesWidth + header.ColumnSpacing;
                var maxLabel = Math.Max(0, available - other.Sum(child => child.ActualWidth) - state.Spacing * other.Length);
                if (Math.Abs(label.MaxWidth - maxLabel) > .5 || double.IsPositiveInfinity(label.MaxWidth)) label.MaxWidth = maxLabel;
            })) paneHeaderLayoutQueued = false;
        }

        private bool HeaderFitsSmoke(bool narrow)
        {
            if (paneHeader is not { ActualWidth: > 0 } header || modeSwitch is null || Grid.GetRow(modeSwitch) != (narrow ? 1 : 0)) return false;
            return new FrameworkElement[] { paneHeaderCopy!, modeDefaultButton!, modeMightyButton!, statusLineToggle }.Where(control => control.Visibility == Visibility.Visible).All(control =>
            {
                if (!control.IsLoaded || control.ActualWidth <= 0 || control.ActualHeight <= 0) return false;
                var start = control.TransformToVisual(header).TransformPoint(new Point());
                var end = control.TransformToVisual(header).TransformPoint(new Point(control.ActualWidth, control.ActualHeight));
                return start.X >= -1 && end.X <= header.ActualWidth + 1 && start.Y >= -1 && end.Y <= header.ActualHeight + 1;
            });
        }
    }
}
