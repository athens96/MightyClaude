using System.Diagnostics;
using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

internal sealed class ScreenShareReferenceSceneWindow
{
    private readonly Func<string, Task> announce;
    private readonly Stopwatch clock = new();
    private TextBlock label = null!, terminal = null!;
    private TranslateTransform scroll = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Window? window;
    private string phase = "done";
    private int previousTypedCount = -1;
    private DateTimeOffset startedAt;
    private Task announcements = Task.CompletedTask;
    private static readonly string Script = string.Concat(Enumerable.Range(1, 400).Select(step => $"$ swift build --target Module{step}\n[{step}/400] Compiling Module{step} Source{step % 17}.swift\n"));
    internal ScreenShareReferenceSceneWindow(Func<string, Task> announce) { this.announce = announce; timer.Tick += (_, _) => Tick(); }
    internal void Show()
    {
        Close(); var next = new Window { Title = Locale.Get("screenShare.scene.windowTitle") }; window = next;
        label = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        terminal = new() { Foreground = new SolidColorBrush(Colors.Lime), FontFamily = new FontFamily("Consolas"), FontSize = 13, IsTextSelectionEnabled = false };
        scroll = new();
        var root = new Grid { Background = new SolidColorBrush(Colors.White), RequestedTheme = ElementTheme.Light };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var header = new Grid { Padding = new Thickness(16, 10, 16, 10), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        AutomationProperties.SetAutomationId(label, "screen-share-scene-countdown"); header.Children.Add(label);
        var close = new Button { Content = Locale.Get("screenShare.scene.stopButton") }; close.Click += (_, _) => Close(); AutomationProperties.SetAutomationId(close, "screen-share-scene-stop"); Grid.SetColumn(close, 1); header.Children.Add(close); root.Children.Add(header);
        var stage = new Grid(); stage.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); stage.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); Grid.SetRow(stage, 1); root.Children.Add(stage);
        var documentHost = new Grid { Background = new SolidColorBrush(Colors.White) };
        var document = new StackPanel { RenderTransform = scroll, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(28, 0, 28, 0) };
        for (var index = 0; index < 80; index++)
        {
            var number = (index % 40 + 1).ToString(); var values = new Dictionary<string, string> { ["number"] = number }; var section = new StackPanel { Height = 150, Spacing = 8 };
            section.Children.Add(new TextBlock { Text = Locale.Get("screenShare.scene.documentHeading", values), FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Colors.Black) });
            section.Children.Add(new TextBlock { Text = Locale.Get("screenShare.scene.documentParagraph", values), FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxLines = 4, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 38, 38, 38)) }); document.Children.Add(section);
        }
        documentHost.Children.Add(document); documentHost.SizeChanged += (_, _) => documentHost.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, documentHost.ActualWidth, documentHost.ActualHeight) }; stage.Children.Add(documentHost);
        var terminalHost = new Border { Padding = new Thickness(14), Background = new SolidColorBrush(Colors.Black), Child = terminal }; Grid.SetColumn(terminalHost, 1); stage.Children.Add(terminalHost);
        next.Content = root;
        var area = DisplayArea.Primary.WorkArea; var insetX = (int)(area.Width * .08); var insetY = (int)(area.Height * .08);
        next.AppWindow.MoveAndResize(new(area.X + insetX, area.Y + insetY, area.Width - insetX * 2, area.Height - insetY * 2));
        if (next.AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = true;
        next.Closed += (_, _) => { if (window == next) { StopClock(); window = null; } };
        previousTypedCount = -1; startedAt = DateTimeOffset.Now; clock.Restart(); phase = "preroll"; Announce(phase); RenderStage(0); Tick();
        CompositionTarget.Rendering += RenderMotion; timer.Start(); next.Activate();
    }
    private void Tick()
    {
        if (window is null) return;
        var moment = ScreenShareReferenceScene.At(clock.Elapsed.TotalSeconds);
        if (moment.Phase != phase) { phase = moment.Phase; Announce(phase); RenderStage(moment.MotionElapsed); }
        var text = phase switch
        {
            "preroll" => Locale.Get("screenShare.scene.prerollLabel", new Dictionary<string, string> { ["seconds"] = moment.SecondsLeft.ToString() }),
            "motion" => Locale.Get("screenShare.scene.motionLabel", new Dictionary<string, string> { ["seconds"] = moment.SecondsLeft.ToString() }),
            "still" => Locale.Get("screenShare.scene.stillLabel", new Dictionary<string, string> { ["time"] = startedAt.AddSeconds(93).ToString("HH:mm:ss") }),
            _ => Locale.Get("screenShare.scene.doneLabel"),
        };
        if (label.Text != text) label.Text = text;
        if (phase == "done") { timer.Stop(); CompositionTarget.Rendering -= RenderMotion; clock.Stop(); }
    }
    private void RenderMotion(object? sender, object args) { if (phase == "motion") RenderStage(ScreenShareReferenceScene.At(clock.Elapsed.TotalSeconds).MotionElapsed); }
    private void RenderStage(double motionElapsed)
    {
        var offset = -ScreenShareReferenceScene.ScrollOffset(motionElapsed, 6000); if (scroll.Y != offset) scroll.Y = offset;
        var count = ScreenShareReferenceScene.TypedCount(motionElapsed) % Script.Length; if (count == previousTypedCount) return; previousTypedCount = count;
        var begin = count; var lines = 0;
        while (begin > 0 && lines < 28) { begin--; if (Script[begin] == '\n') lines++; }
        if (lines == 28) begin++;
        terminal.Text = Script[begin..count] + "▌";
    }
    private void Announce(string next)
    {
        // Keep ordered phase announcements even if the encrypted peer is slow.
        announcements = NotifyAfter(announcements, next);
    }
    private async Task NotifyAfter(Task previous, string next)
    {
        try { await previous; await announce(next); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    private void StopClock()
    {
        timer.Stop(); CompositionTarget.Rendering -= RenderMotion; clock.Stop();
        if (phase != "done") { phase = "done"; Announce(phase); }
    }
    internal void Close()
    {
        var previous = window; window = null; StopClock(); previous?.Close();
    }
}
