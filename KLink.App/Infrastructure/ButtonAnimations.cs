using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KLink.App.Infrastructure;

/// <summary>
/// 按钮微动画附加属性：hover 时轻微变亮/变暗，按下时缩放 0.97。
/// 用法：animations:ButtonAnimations.Enabled="True"（作用于 ui:Button）。
/// </summary>
public static class ButtonAnimations
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ButtonAnimations), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button button)
            return;
        if ((bool)e.NewValue)
        {
            button.MouseEnter += OnEnter;
            button.MouseLeave += OnLeave;
            button.MouseLeftButtonDown += OnDown;
            button.MouseLeftButtonUp += OnUp;
        }
        else
        {
            button.MouseEnter -= OnEnter;
            button.MouseLeave -= OnLeave;
            button.MouseLeftButtonDown -= OnDown;
            button.MouseLeftButtonUp -= OnUp;
        }
    }

    private static void OnEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Button b || !b.IsEnabled)
            return;
        b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, 0.86, TimeSpan.FromMilliseconds(120)));
    }

    private static void OnLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Button b)
            return;
        b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.86, 1.0, TimeSpan.FromMilliseconds(180)));
    }

    private static void OnDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Button b)
            return;
        EnsureScale(b);
        b.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 0.97, TimeSpan.FromMilliseconds(80)));
        b.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 0.97, TimeSpan.FromMilliseconds(80)));
    }

    private static void OnUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Button b)
            return;
        EnsureScale(b);
        b.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1.0, TimeSpan.FromMilliseconds(120)));
        b.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1.0, TimeSpan.FromMilliseconds(120)));
    }

    private static void EnsureScale(Button b)
    {
        if (b.RenderTransform is not ScaleTransform)
        {
            b.RenderTransform = new ScaleTransform(1, 1);
            b.RenderTransformOrigin = new Point(0.5, 0.5);
        }
    }
}
