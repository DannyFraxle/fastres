using System.Windows;

namespace FastRes;

/// <summary>Attached properties used by the control templates in App.xaml.</summary>
public static class Ui
{
    public static readonly DependencyProperty IsCurrentProperty =
        DependencyProperty.RegisterAttached("IsCurrent", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

    public static bool GetIsCurrent(DependencyObject d) => (bool)d.GetValue(IsCurrentProperty);
    public static void SetIsCurrent(DependencyObject d, bool value) => d.SetValue(IsCurrentProperty, value);
}

public sealed record ResolutionItem(int Width, int Height, string Label, string Aspect, bool IsRecommended, bool IsCurrent);

public sealed record RateItem(int Hz, string Label, bool IsCurrent);

public enum ColorKind { OutputBpc, Depth, Sdr, Hdr }

public sealed record ColorItem(ColorKind Kind, int Value, string Label, bool IsCurrent, string Tooltip);

public sealed record MonitorItem(string DeviceName, string Number, string Name, bool IsSelected, string Tooltip);
