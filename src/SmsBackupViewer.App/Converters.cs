using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SmsBackupViewer.App;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

/// <summary>Hides an element when its bound text is empty.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string text && text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Negates a boolean, for controls disabled while a load is running.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>Right-aligns outgoing messages, left-aligns everything else.</summary>
public sealed class OutgoingAlignmentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Bubble fill: brand tint for sent, neutral for received.</summary>
public sealed class OutgoingBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Sent = new((Color)ColorConverter.ConvertFromString("#e3f7fd"));
    private static readonly SolidColorBrush Received = new((Color)ColorConverter.ConvertFromString("#f5f5f5"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Sent : Received;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class OutgoingBorderConverter : IValueConverter
{
    private static readonly SolidColorBrush Sent = new((Color)ColorConverter.ConvertFromString("#03c4eb"));
    private static readonly SolidColorBrush Received = new((Color)ColorConverter.ConvertFromString("#cccccc"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Sent : Received;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Loads an extracted image from disk for inline display.
///
/// Decoding is capped at 480px wide and the file handle is closed immediately, so a conversation
/// full of photographs does not hold the originals in memory or lock the media folder.
/// </summary>
public sealed class MediaPathToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 480;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // A corrupt or unsupported payload should show as a missing image, not crash the view.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True when the path points at a file that exists and can be shown inline.</summary>
public sealed class ImageAvailableConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string path && !string.IsNullOrEmpty(path) && File.Exists(path)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
