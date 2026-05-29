using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PortChecker.ViewModels;

namespace PortChecker;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModelPropertyChanged;
        SourceInitialized += MainWindowSourceInitialized;
        Closed += MainWindowClosed;
        Loaded += MainWindowLoaded;
    }

    private async void MainWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindowLoaded;
        await _viewModel.InitializeAsync();
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsDarkMode))
        {
            ApplyTheme(_viewModel.IsDarkMode);
        }
    }

    private void MainWindowSourceInitialized(object? sender, EventArgs e)
    {
        ApplyWindowChromeTheme(_viewModel.IsDarkMode);
    }

    private void MainWindowClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModelPropertyChanged;
        SourceInitialized -= MainWindowSourceInitialized;
        Closed -= MainWindowClosed;
    }

    private void ApplyTheme(bool isDarkMode)
    {
        var palette = isDarkMode ? ThemePalette.Dark : ThemePalette.Light;

        SetBrush("InkBrush", palette.Ink);
        SetBrush("MutedBrush", palette.Muted);
        SetBrush("SubtleBrush", palette.Subtle);
        SetBrush("LineBrush", palette.Line);
        SetBrush("WindowBackgroundBrush", palette.WindowBackground);
        SetBrush("PanelBrush", palette.Panel);
        SetBrush("PanelAltBrush", palette.PanelAlt);
        SetBrush("ControlBrush", palette.Control);
        SetBrush("SecondaryButtonHoverBrush", palette.SecondaryButtonHover);
        SetBrush("DataGridHeaderBrush", palette.DataGridHeader);
        SetBrush("DataGridAltRowBrush", palette.DataGridAltRow);
        SetBrush("RowBorderBrush", palette.RowBorder);
        SetBrush("PermissionPanelBrush", palette.PermissionPanel);
        SetBrush("PermissionBorderBrush", palette.PermissionBorder);
        SetBrush("BadgeBrush", palette.Badge);
        SetBrush("EmptyPanelBrush", palette.EmptyPanel);
        SetBrush("PlaceholderBrush", palette.Placeholder);
        SetBrush("WarningBorderBrush", palette.WarningBorder);
        SetBrush("WarningTextBrush", palette.WarningText);
        SetBrush("InfoPanelBrush", palette.InfoPanel);
        SetBrush("InfoBorderBrush", palette.InfoBorder);
        SetBrush("InfoTextBrush", palette.InfoText);
        SetBrush("DangerHoverBrush", palette.DangerHover);
        SetBrush("HoverBrush", palette.Hover);
        SetBrush("SelectedBrush", palette.Selected);
        SetBrush("AccentSoftBrush", palette.AccentSoft);
        SetBrush("SuccessBrush", palette.Success);
        SetBrush("SuccessSoftBrush", palette.SuccessSoft);
        SetBrush("WarningBrush", palette.Warning);
        SetBrush("WarningSoftBrush", palette.WarningSoft);

        Background = (Brush)Resources["WindowBackgroundBrush"];
        ApplyWindowChromeTheme(isDarkMode);
    }

    private void SetBrush(string resourceKey, Color color)
    {
        if (Resources[resourceKey] is SolidColorBrush brush)
        {
            brush.Color = color;
        }
    }

    private void ApplyWindowChromeTheme(bool isDarkMode)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = isDarkMode ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;

    private readonly record struct ThemePalette(
        Color Ink,
        Color Muted,
        Color Subtle,
        Color Line,
        Color WindowBackground,
        Color Panel,
        Color PanelAlt,
        Color Control,
        Color SecondaryButtonHover,
        Color DataGridHeader,
        Color DataGridAltRow,
        Color RowBorder,
        Color PermissionPanel,
        Color PermissionBorder,
        Color Badge,
        Color EmptyPanel,
        Color Placeholder,
        Color WarningBorder,
        Color WarningText,
        Color InfoPanel,
        Color InfoBorder,
        Color InfoText,
        Color DangerHover,
        Color Hover,
        Color Selected,
        Color AccentSoft,
        Color Success,
        Color SuccessSoft,
        Color Warning,
        Color WarningSoft)
    {
        public static ThemePalette Light { get; } = new(
            Color.FromRgb(0x11, 0x18, 0x27),
            Color.FromRgb(0x66, 0x70, 0x85),
            Color.FromRgb(0x98, 0xA2, 0xB3),
            Color.FromRgb(0xDC, 0xE5, 0xF2),
            Color.FromRgb(0xF6, 0xF8, 0xFC),
            Color.FromRgb(0xFF, 0xFF, 0xFF),
            Color.FromRgb(0xF8, 0xFA, 0xFD),
            Color.FromRgb(0xFD, 0xFE, 0xFF),
            Color.FromRgb(0xF3, 0xF7, 0xFD),
            Color.FromRgb(0xF6, 0xF9, 0xFD),
            Color.FromRgb(0xFB, 0xFC, 0xFE),
            Color.FromRgb(0xEE, 0xF2, 0xF7),
            Color.FromRgb(0xF8, 0xFB, 0xFF),
            Color.FromRgb(0xCF, 0xE0, 0xFF),
            Color.FromRgb(0xEA, 0xF2, 0xFF),
            Color.FromRgb(0xF8, 0xFA, 0xFD),
            Color.FromRgb(0x8D, 0x98, 0xAA),
            Color.FromRgb(0xFE, 0xD7, 0xAA),
            Color.FromRgb(0x9A, 0x34, 0x12),
            Color.FromRgb(0xEE, 0xF6, 0xFF),
            Color.FromRgb(0xBF, 0xDB, 0xFE),
            Color.FromRgb(0x1E, 0x40, 0xAF),
            Color.FromRgb(0xB9, 0x1C, 0x1C),
            Color.FromRgb(0xF2, 0xF7, 0xFF),
            Color.FromRgb(0xEA, 0xF2, 0xFF),
            Color.FromRgb(0xEE, 0xF5, 0xFF),
            Color.FromRgb(0x3F, 0xB7, 0x73),
            Color.FromRgb(0xEA, 0xF8, 0xF1),
            Color.FromRgb(0xF9, 0x73, 0x16),
            Color.FromRgb(0xFF, 0xF2, 0xE8));

        public static ThemePalette Dark { get; } = new(
            Color.FromRgb(0xF8, 0xFA, 0xFC),
            Color.FromRgb(0xA7, 0xB2, 0xC3),
            Color.FromRgb(0x73, 0x80, 0x93),
            Color.FromRgb(0x27, 0x34, 0x46),
            Color.FromRgb(0x09, 0x10, 0x17),
            Color.FromRgb(0x10, 0x18, 0x22),
            Color.FromRgb(0x0D, 0x15, 0x1E),
            Color.FromRgb(0x0C, 0x14, 0x1D),
            Color.FromRgb(0x17, 0x23, 0x31),
            Color.FromRgb(0x17, 0x20, 0x2B),
            Color.FromRgb(0x0E, 0x16, 0x20),
            Color.FromRgb(0x20, 0x2C, 0x3B),
            Color.FromRgb(0x0F, 0x18, 0x24),
            Color.FromRgb(0x25, 0x37, 0x4D),
            Color.FromRgb(0x12, 0x35, 0x61),
            Color.FromRgb(0x0F, 0x17, 0x21),
            Color.FromRgb(0x78, 0x86, 0x99),
            Color.FromRgb(0x92, 0x40, 0x0E),
            Color.FromRgb(0xFD, 0xBA, 0x74),
            Color.FromRgb(0x10, 0x24, 0x3F),
            Color.FromRgb(0x1D, 0x4E, 0x89),
            Color.FromRgb(0x93, 0xC5, 0xFD),
            Color.FromRgb(0x99, 0x1B, 0x1B),
            Color.FromRgb(0x15, 0x24, 0x39),
            Color.FromRgb(0x17, 0x34, 0x5A),
            Color.FromRgb(0x10, 0x2A, 0x52),
            Color.FromRgb(0x34, 0xD3, 0x99),
            Color.FromRgb(0x0F, 0x2A, 0x1F),
            Color.FromRgb(0xFB, 0x92, 0x3C),
            Color.FromRgb(0x2A, 0x18, 0x0B));
    }
}
