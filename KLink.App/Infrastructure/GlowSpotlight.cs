using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KLink.App.Infrastructure;

/// <summary>
/// 按钮鼠标光圈：悬浮时按钮表面出现以鼠标为圆心的微弱发光（白色核心 + 翡翠边缘），
/// 光晕通过模板 Glow 层的 Margin="-4" 溢出到按钮外形成边框辉光。
/// 边框变亮由各按钮样式的 Style.Triggers 完成（瞬时替换，无动画开销）。
/// 用法：glow:GlowSpotlight.Enabled="True"
/// </summary>
public static class GlowSpotlight
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(GlowSpotlight), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button button)
            return;
        if ((bool)e.NewValue)
            button.Loaded += OnLoaded;
        else
            button.Loaded -= OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.Template?.FindName("Glow", button) is not Border glow)
            return; // 模板没有 Glow 元素则跳过

        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(0, 0),
            GradientOrigin = new Point(0, 0),
            RadiusX = 200,
            RadiusY = 200,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(18, 255, 255, 255), 0.0),
                new GradientStop(Color.FromArgb(7, 160, 255, 210), 0.45),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0),
            },
        };
        glow.Background = brush;
        glow.Opacity = 0;
        glow.IsHitTestVisible = false;

        // 光圈半径跟随按钮尺寸：覆盖整个按钮（鼠标处最亮，向四周渐隐）
        void UpdateRadius()
        {
            double r = Math.Max(button.RenderSize.Width, button.RenderSize.Height) * 0.9;
            r = Math.Max(r, 60);
            brush.RadiusX = r;
            brush.RadiusY = r;
        }
        UpdateRadius();
        button.SizeChanged += (_, _) => UpdateRadius();

        // 位置节流：移动小于 2px 不更新，避免高频重绘
        Point lastPos = new(double.NaN, double.NaN);
        void UpdateCenter(MouseEventArgs me)
        {
            var pos = me.GetPosition(button);
            if (Math.Abs(pos.X - lastPos.X) < 2 && Math.Abs(pos.Y - lastPos.Y) < 2)
                return;
            lastPos = pos;
            brush.Center = pos;
            brush.GradientOrigin = pos;
        }

        button.MouseMove += (_, me) => UpdateCenter(me);
        button.MouseEnter += (_, me) =>
        {
            UpdateCenter(me);
            glow.Opacity = 1;
        };
        button.MouseLeave += (_, _) => glow.Opacity = 0;
        button.IsEnabledChanged += (_, _) => glow.Opacity = button.IsMouseOver && button.IsEnabled ? 1 : 0;
    }
}
