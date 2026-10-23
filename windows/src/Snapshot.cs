using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace AgentWheel;

/// <summary>Offscreen renders of the overlay and banners, for checking the look without a real drag.</summary>
public static class Snapshot
{
    public static readonly Size DefaultSize = new(1512, 982);

    public static void Wheel(IList<string> files, int? hover, int page, string? background, string output,
        Size? size = null, Point? center = null, Wanted? wanted = null)
    {
        var s = size ?? DefaultSize;
        var root = new Grid { Width = s.Width, Height = s.Height };
        root.Children.Add(Backdrop(background, s, true));

        var content = new OverlayContent();
        root.Children.Add(content);
        var context = Catalog.Context(files);
        page = Math.Clamp(page, 0, Math.Max(0, context.Pages.Count - 1));
        content.Arrange(s, center ?? new Point(s.Width / 2, s.Height / 2), SizeInfo.Radius(Settings.Size), context.StatLabels.Length);
        content.SetAccent(Theme.Accent);
        content.Apply(context, page, hover);
        WheelOption? option = hover is int h && page < context.Pages.Count && h < context.Pages[page].Slots.Count ? context.Pages[page].Slots[h] : null;
        if (hover is int glow && option != null) content.Wheel.Glow[glow] = option.Enabled ? 1 : 0.35;
        content.Stats.Shown = content.Stats.Target.ToArray();
        wanted ??= new Wanted();
        content.Wanted.Show(wanted);
        content.Wanted.Visibility = wanted.Level > 0 ? Visibility.Visible : Visibility.Hidden;
        content.Wanted.Appear = 1;
        content.Wanted.Phase = 0.1;
        if (option != null && option.Id.EndsWith(".strip"))
        {
            content.Wanted.Evading = true;
            content.Wanted.Caption = "鬆手 = 解除偵查";
        }
        Write(root, s, output);
    }

    public static void Banner(bool passed, string title, string detail, string? background, string output,
        Size? size = null, Outcome? outcome = null)
    {
        var s = size ?? DefaultSize;
        var root = new Canvas { Width = s.Width, Height = s.Height };
        var back = Backdrop(background, s, false);
        root.Children.Add(back);
        var banner = new BannerVisual { FullScreen = !passed, Progress = 1 };
        banner.Configure(passed, title, detail, outcome);
        if (passed)
        {
            banner.Width = s.Width;
            banner.Height = BannerVisual.BandHeight;
            Canvas.SetTop(banner, s.Height / 2 - BannerVisual.BandHeight / 2 - 40);
        }
        else
        {
            banner.Width = s.Width;
            banner.Height = s.Height;
        }
        root.Children.Add(banner);
        Write(root, s, output);
    }

    /// <summary>Renders an element at 2× and saves it as PNG.</summary>
    public static void Write(FrameworkElement root, Size size, string output)
    {
        byte[] png = Png(root, size, 2);
        File.WriteAllBytes(output, png);
    }

    public static byte[] Png(FrameworkElement root, Size size, double scale)
    {
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static BitmapSource LoadBitmap(byte[] bytes)
    {
        var image = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>A desktop picture (blurred like the live overlay) or the default navy-and-red backdrop.</summary>
    private static FrameworkElement Backdrop(string? path, Size size, bool blurred)
    {
        if (path != null && File.Exists(path))
        {
            var image = new Image
            {
                Source = LoadBitmap(File.ReadAllBytes(path)),
                Stretch = Stretch.UniformToFill,
                Width = size.Width,
                Height = size.Height,
            };
            if (blurred) image.Effect = new BlurEffect { Radius = 40, KernelType = KernelType.Gaussian };
            var holder = new Grid { Width = size.Width, Height = size.Height, ClipToBounds = true, Background = Brushes.Black };
            holder.Children.Add(image);
            if (blurred) holder.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)) });
            return holder;
        }
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(1, 0) };
        gradient.GradientStops.Add(new GradientStop(Theme.Navy.Mix(Colors.White, 0.12), 0));
        gradient.GradientStops.Add(new GradientStop(Theme.Navy, 0.55));
        gradient.GradientStops.Add(new GradientStop(Theme.Red.Mix(Theme.Navy, 0.55), 1));
        return new Rectangle { Width = size.Width, Height = size.Height, Fill = gradient };
    }
}

/// <summary>Image compositions drawn with WPF (must run on an STA thread).</summary>
public static class Render
{
    /// <summary>加背景: warm gradient backdrop with padding, rounded corners and a soft shadow, as PNG bytes.</summary>
    public static byte[] Backdrop(byte[] png, int w, int h)
    {
        double pad = Math.Round(Math.Max(w, h) * 0.09);
        double width = w + pad * 2, height = h + pad * 2;
        var root = new Canvas { Width = width, Height = height };

        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(250, 89, 128), 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(252, 140, 92), 0.55));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(252, 201, 102), 1));
        root.Children.Add(new Rectangle { Width = width, Height = height, Fill = gradient });

        double corner = Math.Min(w, h) * 0.035;
        var card = new Rectangle
        {
            Width = w,
            Height = h,
            RadiusX = corner,
            RadiusY = corner,
            Fill = Brushes.White,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.35,
                BlurRadius = pad * 0.9,
                ShadowDepth = pad * 0.12,
                Direction = 270,
                RenderingBias = RenderingBias.Quality,
            },
        };
        Canvas.SetLeft(card, pad);
        Canvas.SetTop(card, pad);
        root.Children.Add(card);

        var photo = new Image
        {
            Source = Snapshot.LoadBitmap(png),
            Width = w,
            Height = h,
            Stretch = Stretch.Fill,
            Clip = new RectangleGeometry(new Rect(0, 0, w, h), corner, corner),
        };
        RenderOptions.SetBitmapScalingMode(photo, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(photo, pad);
        Canvas.SetTop(photo, pad);
        root.Children.Add(photo);

        return Snapshot.Png(root, new Size(width, height), 1);
    }
}
