using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;

namespace MobileEssControl.Controls.Gauges;

public class SemiArcGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SemiArcGauge, double>(
            nameof(Value),
            0);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<SemiArcGauge, double>(
            nameof(Minimum),
            0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<SemiArcGauge, double>(
            nameof(Maximum),
            100);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<SemiArcGauge, double>(
            nameof(StrokeThickness),
            14);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SemiArcGauge, IBrush?>(
            nameof(TrackBrush),
            new SolidColorBrush(Color.Parse("#E2E8F0")));

    public static readonly StyledProperty<IBrush?> ProgressBrushProperty =
        AvaloniaProperty.Register<SemiArcGauge, IBrush?>(
            nameof(ProgressBrush),
            new SolidColorBrush(Color.Parse("#22C55E")));

    public static readonly StyledProperty<bool> ShowEndDotProperty =
        AvaloniaProperty.Register<SemiArcGauge, bool>(
            nameof(ShowEndDot),
            true);

    static SemiArcGauge()
    {
        AffectsRender<SemiArcGauge>(
            ValueProperty,
            MinimumProperty,
            MaximumProperty,
            StrokeThicknessProperty,
            TrackBrushProperty,
            ProgressBrushProperty,
            ShowEndDotProperty);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? ProgressBrush
    {
        get => GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public bool ShowEndDot
    {
        get => GetValue(ShowEndDotProperty);
        set => SetValue(ShowEndDotProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double width = Bounds.Width;
        double height = Bounds.Height;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        double thickness = StrokeThickness;
        double radius = Math.Min(
            (width - thickness) / 2.0,
            height - thickness);

        if (radius <= 0)
        {
            return;
        }

        Point center = new(
            width / 2.0,
            height - thickness / 2.0);

        double startAngle = Math.PI;
        double endAngle = Math.PI * 2.0;

        Point startPoint = GetPoint(center, radius, startAngle);
        Point endPoint = GetPoint(center, radius, endAngle);

        var trackPen = new Pen(
            TrackBrush,
            thickness,
            null,
            PenLineCap.Round);

        DrawArc(
            context,
            trackPen,
            startPoint,
            endPoint,
            radius);

        double ratio = GetRatio();

        if (ratio <= 0)
        {
            return;
        }

        double progressAngle =
            startAngle + Math.PI * ratio;

        Point progressEndPoint =
            GetPoint(center, radius, progressAngle);

        var progressPen = new Pen(
            ProgressBrush,
            thickness,
            null,
            PenLineCap.Round);

        DrawArc(
            context,
            progressPen,
            startPoint,
            progressEndPoint,
            radius);

        if (ShowEndDot)
        {
            double dotRadius = thickness * 0.46;

            context.DrawEllipse(
                ProgressBrush,
                null,
                progressEndPoint,
                dotRadius,
                dotRadius);
        }
    }

    private double GetRatio()
    {
        if (Maximum <= Minimum)
        {
            return 0;
        }

        double value = Math.Clamp(Value, Minimum, Maximum);

        return (value - Minimum) / (Maximum - Minimum);
    }

    private static Point GetPoint(
        Point center,
        double radius,
        double angle)
    {
        return new Point(
            center.X + radius * Math.Cos(angle),
            center.Y + radius * Math.Sin(angle));
    }

    private static void DrawArc(
        DrawingContext context,
        Pen pen,
        Point startPoint,
        Point endPoint,
        double radius)
    {
        var geometry = new StreamGeometry();

        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(
                startPoint,
                isFilled: false);

            geometryContext.ArcTo(
                endPoint,
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: false,
                sweepDirection: SweepDirection.Clockwise);

            geometryContext.EndFigure(
                isClosed: false);
        }

        context.DrawGeometry(
            brush: null,
            pen: pen,
            geometry: geometry);
    }
}