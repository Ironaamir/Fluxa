using System.Diagnostics;
using Fluxa.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace Fluxa.App;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ImageConversionEngine _engine = new();
    private readonly FileQueue<QueueItem> _files = new(item => item.Path, item => item.Complete);
    private List<QueueItem> _queue => _files.Items;
    private static readonly Dictionary<string, SolidColorBrush> ThemeBrushes = [];
    private readonly Dictionary<(ConversionMode Mode, bool Compression), Button> _modeButtons = [];
    private ConversionMode _currentMode = ConversionMode.Images;
    private bool _compression;
    private readonly StackPanel _completedPanel = new() { Spacing = 8 };
    private readonly StackPanel _completedSection = new() { Spacing = 12 };
    private readonly TextBlock _completedCount = Text("FINISHED · 0", 12, "Muted");
    private readonly TextBlock _emptyCompleted = Text("Converted files will appear here.", 14, "Muted");
    private readonly StackPanel _queuePanel = new() { Spacing = 8 };
    private readonly StackPanel _imagePanel = new() { Spacing = 16 };
    private readonly StackPanel _futurePanel = new() { Spacing = 20 };
    private readonly StackPanel _actionPanel = new() { Spacing = 10, Margin = new Thickness(32, 10, 32, 20) };
    private readonly TextBlock _subtitle = Text("Image conversion", 15, "Muted");
    private readonly TextBlock _heading = Text("Convert images", 34);
    private readonly TextBlock _queueCount = Text("QUEUE · 0", 12, "Muted");
    private readonly TextBlock _emptyQueue = Text("No files yet. Add images above to get started.", 14, "Muted");
    private readonly TextBlock _futureTitle = Text("Video conversion is on the way", 28);
    private readonly TextBlock _futureDescription = Text("", 15, "Muted");
    private readonly ComboBox _format = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _frames = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Slider _quality = new() { Minimum = 1, Maximum = 100, StepFrequency = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _qualityValue = Text("90%", 14, "AccentText");
    private readonly CheckBox _strip = new() { Content = "Remove metadata (EXIF, location, profiles)", IsChecked = true };
    private readonly TextBox _output = new() { IsReadOnly = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _convert = new() { Content = "Convert images", MinWidth = 170 };
    private readonly Button _cancel = new() { Content = "Cancel remaining", Visibility = Visibility.Collapsed };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true };
    private readonly List<Control> _busyDisabled = [];
    private readonly nint _hwnd;
    private TrayIcon? _tray;
    private CancellationTokenSource? _conversion;
    private bool _exiting;
    private bool _ready;
    private readonly Queue<LaunchRequest> _launches = new();
    private bool _handlingLaunches;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Fluxa";
        _settings = AppSettings.Load(out var warning);
        _hwnd = WindowNative.GetWindowHandle(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 850));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ApplyPalette();
        BuildLayout();
        _format.ItemsSource = ImageConversionEngine.OutputFormats;
        _format.SelectedItem = ImageConversionEngine.OutputFormats.FirstOrDefault(f => f.Extension == _settings.OutputExtension)
            ?? ImageConversionEngine.OutputFormats[0];
        _frames.Items.Add("Preserve frames / pages");
        _frames.Items.Add("First frame only");
        _frames.SelectedIndex = _settings.Frames == FramePolicy.FirstFrame ? 1 : 0;
        _quality.Value = _settings.Quality;
        _strip.IsChecked = _settings.StripMetadata;
        _output.Text = _settings.OutputFolder;
        _format.SelectionChanged += (_, _) => { UpdateQuality(); SavePreferences(); };
        _frames.SelectionChanged += (_, _) => SavePreferences();
        _quality.ValueChanged += (_, _) => { _qualityValue.Text = $"{(int)_quality.Value}%"; SavePreferences(); };
        _strip.Checked += (_, _) => SavePreferences();
        _strip.Unchecked += (_, _) => SavePreferences();
        _convert.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _convert.Click += async (_, _) => await ConvertQueueAsync();
        _cancel.Click += (_, _) => { _conversion?.Cancel(); _cancel.IsEnabled = false; _cancel.Content = "Stopping after current codec step…"; };
        try { _tray = new TrayIcon(_hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), OpenFromTray, Exit); }
        catch (Exception ex) { Notify("Tray icon unavailable", ex.Message, InfoBarSeverity.Warning); }
        AppWindow.Closing += (_, args) =>
        {
            if (_exiting) return;
            if (_conversion != null || (_settings.CloseToTray && _tray != null))
            {
                args.Cancel = true;
                if (_tray != null) AppWindow.Hide();
                else Notify("Conversion is still running", "Cancel the batch before closing. The tray icon is unavailable, so the window will stay visible.", InfoBarSeverity.Warning);
            }
        };
        Closed += (_, _) => { _conversion?.Cancel(); _tray?.Dispose(); CleanupCloud(); };
        _ready = true;
        SwitchMode(ConversionMode.Images);
        UpdateQuality();
        UpdateQueue();
        if (warning != null) Notify("Preferences", warning, InfoBarSeverity.Warning);
        try { ExplorerIntegration.Apply(_settings.ContextMenuEnabled, _settings.ContextMenuExtension); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Notify("Explorer commands unavailable", ex.Message, InfoBarSeverity.Warning); }
    }

    private void ApplyPalette()
    {
        var colors = ThemePalette.Find(_settings.AccentColor).Colors(_settings.LightMode);
        foreach (var (key, value) in colors)
        {
            Brush(key).Color = ColorOf(value);
            Application.Current.Resources[key + "Brush"] = Brush(key);
        }
        Root.RequestedTheme = _settings.LightMode ? ElementTheme.Light : ElementTheme.Dark;
        Root.Background = Brush("Background");
        // App scope includes ContentDialog and popup trees; named state resources avoid
        // stock StaticResource aliases capturing the Windows accent before our overrides.
        var resources = Application.Current.Resources;
        foreach (var key in new[] { "AccentFillColorDefaultBrush", "AccentButtonBackground", "AccentFillColorSelectedTextBackgroundBrush", "AccentAAFillColorDefaultBrush", "SliderThumbBackground", "SliderTrackValueFill", "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundStrokeChecked", "ToggleSwitchFillOn", "ToggleSwitchStrokeOn", "ProgressBarForeground", "GridViewItemSelectedBorderBrush", "GridViewItemCheckBoxSelectedBrush", "ListViewItemSelectionIndicatorBrush", "TextControlBorderBrushFocused", "TextControlElevationBorderFocusedBrush", "SystemControlHighlightAccentBrush", "SystemControlForegroundAccentBrush", "SystemControlBackgroundAccentBrush" })
            resources[key] = Brush("Accent");
        foreach (var key in new[] { "AccentFillColorSecondaryBrush", "AccentButtonBackgroundPointerOver", "AccentAAFillColorSecondaryBrush", "SliderThumbBackgroundPointerOver", "SliderTrackValueFillPointerOver", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "ToggleSwitchFillOnPointerOver", "ToggleSwitchStrokeOnPointerOver", "GridViewItemSelectedPointerOverBorderBrush", "GridViewItemCheckBoxSelectedPointerOverBrush", "ListViewItemSelectionIndicatorPointerOverBrush" })
            resources[key] = Brush("AccentHover");
        foreach (var key in new[] { "AccentFillColorTertiaryBrush", "AccentButtonBackgroundPressed", "AccentAAFillColorTertiaryBrush", "SliderThumbBackgroundPressed", "SliderTrackValueFillPressed", "CheckBoxCheckBackgroundFillCheckedPressed", "CheckBoxCheckBackgroundStrokeCheckedPressed", "ToggleSwitchFillOnPressed", "ToggleSwitchStrokeOnPressed", "GridViewItemSelectedPressedBorderBrush", "GridViewItemCheckBoxSelectedPressedBrush", "ListViewItemSelectionIndicatorPressedBrush" })
            resources[key] = Brush("AccentPressed");
        foreach (var key in new[] { "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush" }) resources[key] = Brush("AccentText");
        foreach (var key in new[] { "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed", "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush", "TextOnAccentFillColorTertiaryBrush", "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed", "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed" })
            resources[key] = Brush("AccentInk");
        foreach (var key in new[] { "ComboBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelectedUnfocused", "ComboBoxItemBackgroundSelectedPointerOver", "ComboBoxItemBackgroundSelectedPressed", "GridViewItemBackgroundSelected", "GridViewItemBackgroundSelectedPointerOver", "GridViewItemBackgroundSelectedPressed", "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed" }) resources[key] = Brush("Selected");
        resources["TextControlSelectionHighlightColor"] = ColorOf(colors["Accent"]);
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" }) resources[key] = ColorOf(colors["Accent"]);
        if (AppWindow.TitleBar is { } bar)
        {
            bar.BackgroundColor = bar.ButtonBackgroundColor = bar.InactiveBackgroundColor = ColorOf(colors["Background"]);
            bar.ForegroundColor = bar.ButtonForegroundColor = bar.InactiveForegroundColor = ColorOf(colors["Text"]);
            bar.ButtonHoverBackgroundColor = ColorOf(colors["NavHover"]);
            bar.ButtonHoverForegroundColor = ColorOf(colors["Text"]);
        }
    }

    private void BuildLayout()
    {
        Root.ColumnDefinitions.Add(new() { Width = new GridLength(242) });
        Root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var sidebar = new Grid { Background = Brush("Surface"), Padding = new Thickness(18, 26, 18, 20) };
        sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var brand = new StackPanel { Spacing = 12 };
        var brandRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        brandRow.Children.Add(new Image { Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"))), Width = 34, Height = 34 });
        brandRow.Children.Add(Text("Fluxa", 18));
        brand.Children.Add(brandRow);
        brand.Children.Add(Text("LESS FRICTION. MORE FORMATS.", 9, "Muted"));
        sidebar.Children.Add(brand);
        var nav = new StackPanel { Spacing = 18, Margin = new Thickness(0, 28, 0, 18) };
        nav.Children.Add(BuildModeGroup(false));
        nav.Children.Add(BuildModeGroup(true));
        var navScroll = new ScrollViewer { Content = nav, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(navScroll, 1);
        sidebar.Children.Add(navScroll);
        var bottom = new StackPanel { Spacing = 10 };
        var settings = ActionButton("Settings", async () => await ShowSettingsAsync());
        var settingsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        settingsRow.Children.Add(new FontIcon { Glyph = "\uE713", FontSize = 18, Foreground = Brush("Text") });
        settingsRow.Children.Add(Text("Settings", 15));
        settings.Content = settingsRow;
        Name(settings, "Settings");
        ConfigureSidebarButton(settings);
        settings.Foreground = Brush("Text");
        settings.HorizontalAlignment = HorizontalAlignment.Stretch;
        bottom.Children.Add(settings);
        bottom.Children.Add(Text("LOCAL BY DEFAULT", 10, "AccentText"));
        bottom.Children.Add(Text("Your files stay on this device.", 11, "Muted"));
        Grid.SetRow(bottom, 2);
        sidebar.Children.Add(bottom);
        Root.Children.Add(sidebar);

        var content = new StackPanel { Spacing = 18, Margin = new Thickness(32, 26, 32, 26), MaxWidth = 1150, HorizontalAlignment = HorizontalAlignment.Stretch };
        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(Text("YOUR FILE WORKSPACE", 11, "AccentText"));
        header.Children.Add(_heading);
        header.Children.Add(_subtitle);
        content.Children.Add(header);
        content.Children.Add(_notice);
        BuildImagePanel();
        content.Children.Add(_imagePanel);
        content.Children.Add(_completedSection);
        var coming = new StackPanel { Spacing = 16, Margin = new Thickness(8, 24, 8, 24) };
        coming.Children.Add(Text("COMING LATER", 12, "AccentText"));
        coming.Children.Add(_futureTitle);
        coming.Children.Add(_futureDescription);
        coming.Children.Add(ActionButton("Back to images", () => { SwitchMode(ConversionMode.Images); return Task.CompletedTask; }));
        _futurePanel.Children.Add(Card(coming));
        _futurePanel.Children.Add(Text("This workspace is reserved for a future engine. No files are processed in this mode yet.", 14, "Muted"));
        content.Children.Add(_futurePanel);
        var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var workspace = new Grid();
        workspace.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        workspace.Children.Add(scroll);
        Grid.SetRow(_actionPanel, 1);
        workspace.Children.Add(_actionPanel);
        Grid.SetColumn(workspace, 1);
        Root.Children.Add(workspace);
    }

    private StackPanel BuildModeGroup(bool compression)
    {
        var expanded = compression ? _settings.CompressionExpanded : _settings.ConversionExpanded;
        var group = new StackPanel { Spacing = 5 };
        var items = new StackPanel { Spacing = 4, Visibility = expanded ? Visibility.Visible : Visibility.Collapsed };
        var arrow = new FontIcon { Glyph = expanded ? "\uE70D" : "\uE76C", FontSize = 10, Foreground = Brush("Muted") };
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerRow.Children.Add(arrow);
        headerRow.Children.Add(Text(compression ? "COMPRESSION MODES" : "CONVERSION MODES", 10, "Muted"));
        var header = new Button { Content = headerRow, Style = (Style)Application.Current.Resources["SidebarButtonStyle"] };
        ConfigureSidebarButton(header);
        var groupName = compression ? "Compression modes" : "Conversion modes";
        Name(header, groupName);
        AutomationProperties.SetItemStatus(header, expanded ? "Expanded" : "Collapsed");
        header.Click += (_, _) =>
        {
            expanded = !expanded;
            items.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            arrow.Glyph = expanded ? "\uE70D" : "\uE76C";
            AutomationProperties.SetItemStatus(header, expanded ? "Expanded" : "Collapsed");
            if (compression) _settings.CompressionExpanded = expanded;
            else _settings.ConversionExpanded = expanded;
            SavePreferences();
        };
        group.Children.Add(header);
        foreach (var (mode, glyph, label) in new[]
        {
            (ConversionMode.Images, "\uEB9F", "Image"), (ConversionMode.Video, "\uE714", "Video"),
            (ConversionMode.Audio, "\uE8D6", "Audio"), (ConversionMode.Pdf, "\uEA90", "PDF")
        })
        {
            var icon = new Grid { Width = 24, Height = 24 };
            icon.Children.Add(new FontIcon { Glyph = glyph, FontSize = compression ? 15 : 18, Foreground = Brush("Text") });
            if (compression)
            {
                // A compact badge distinguishes compression from the conversion icons.
                icon.Children.Add(new Border
                {
                    Width = 13, Height = 13, CornerRadius = new CornerRadius(3), Background = Brush("Accent"),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                    Child = new FontIcon { Glyph = "\uE73F", FontSize = 9, Foreground = Brush("AccentInk") }
                });
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(icon);
            row.Children.Add(Text(label, 15));
            var button = new Button { Content = row, Foreground = Brush("Text"), Style = (Style)Application.Current.Resources["SidebarButtonStyle"] };
            ConfigureSidebarButton(button);
            Name(button, compression ? label + " compression mode" : (mode == ConversionMode.Images ? "Images" : label) + " mode");
            ToolTipService.SetToolTip(button, label + (compression ? " compression · coming later" : mode == ConversionMode.Images ? " conversion" : " conversion · coming later"));
            button.Click += (_, _) => SwitchMode(mode, compression);
            _modeButtons[(mode, compression)] = button;
            items.Children.Add(button);
        }
        group.Children.Add(items);
        return group;
    }

    private void BuildImagePanel()
    {
        var dropContent = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        dropContent.Children.Add(new FontIcon { Glyph = "\uE898", FontSize = 26, Foreground = Brush("AccentText") });
        dropContent.Children.Add(Text("Drop your images here", 22));
        dropContent.Children.Add(Text("Or browse local files. Add one image or a whole batch.", 13, "Muted"));
        var browse = ActionButton("Browse images", BrowseAsync);
        browse.HorizontalAlignment = HorizontalAlignment.Center;
        dropContent.Children.Add(browse);
        _busyDisabled.Add(browse);
        var drop = Card(dropContent);
        drop.Padding = new Thickness(20);
        drop.BorderBrush = Brush("Accent");
        drop.AllowDrop = true;
        Name(drop, "Image drop zone");
        drop.DragOver += (_, args) =>
        {
            args.AcceptedOperation = _conversion == null && args.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;
            args.DragUIOverride.Caption = "Add images to the queue";
            args.DragUIOverride.IsCaptionVisible = true;
        };
        drop.Drop += async (_, args) =>
        {
            if (_conversion != null || !args.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var deferral = args.GetDeferral();
            try { AddFiles((await args.DataView.GetStorageItemsAsync()).OfType<StorageFile>().Select(f => f.Path)); }
            catch (Exception ex) { Notify("Could not add dropped files", ex.Message, InfoBarSeverity.Error); }
            finally { deferral.Complete(); }
        };
        _imagePanel.Children.Add(drop);
        var sources = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        sources.Children.Add(Text("IMPORT FROM", 11, "Muted"));
        foreach (var provider in CloudSources.Providers)
        {
            var button = ActionButton(provider.Name, () => ShowCloudAsync(provider.Kind));
            Name(button, "Import from " + provider.Name);
            ToolTipService.SetToolTip(button, provider.Status);
            _busyDisabled.Add(button);
            sources.Children.Add(button);
        }
        _imagePanel.Children.Add(sources);
        var options = new Grid { ColumnSpacing = 24, RowSpacing = 18 };
        options.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        options.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        options.RowDefinitions.Add(new() { Height = GridLength.Auto });
        options.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var formats = Field("CONVERT TO", _format);
        Name(_format, "Output format");
        options.Children.Add(formats);
        var frameField = Field("ANIMATION & MULTIPAGE", _frames);
        Name(_frames, "Frame handling");
        Grid.SetColumn(frameField, 1);
        options.Children.Add(frameField);
        var quality = new StackPanel { Spacing = 4 };
        quality.Children.Add(Text("QUALITY", 11, "Muted"));
        quality.Children.Add(_quality);
        quality.Children.Add(_qualityValue);
        Name(_quality, "Output quality");
        Grid.SetRow(quality, 1);
        options.Children.Add(quality);
        _strip.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(_strip, 1);
        Grid.SetColumn(_strip, 1);
        options.Children.Add(_strip);
        _busyDisabled.AddRange([_format, _frames, _strip]);
        var optionStack = new StackPanel { Spacing = 18 };
        optionStack.Children.Add(options);
        var folderRow = new Grid { ColumnSpacing = 12 };
        folderRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Name(_output, "Output folder");
        folderRow.Children.Add(_output);
        var chooseFolder = ActionButton("Choose folder", ChooseFolderAsync);
        Grid.SetColumn(chooseFolder, 1);
        folderRow.Children.Add(chooseFolder);
        _busyDisabled.Add(chooseFolder);
        optionStack.Children.Add(Field("SAVE TO", folderRow));
        optionStack.Children.Add(Text("Originals are never overwritten. Duplicate names get a numbered suffix. JPEG uses a white background; icons fit within 256 × 256.", 12, "Muted"));
        _imagePanel.Children.Add(Card(optionStack));
        var queueSection = new StackPanel { Spacing = 12 };
        var queueHeader = new Grid();
        queueHeader.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        queueHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _queueCount.VerticalAlignment = VerticalAlignment.Center;
        queueHeader.Children.Add(_queueCount);
        var clear = ActionButton("Clear queue", () => { _queue.RemoveAll(item => !item.Complete); UpdateQueue(); return Task.CompletedTask; });
        Name(clear, "Clear queue");
        _busyDisabled.Add(clear);
        Grid.SetColumn(clear, 1);
        queueHeader.Children.Add(clear);
        queueSection.Children.Add(queueHeader);
        queueSection.Children.Add(_emptyQueue);
        queueSection.Children.Add(new ScrollViewer { Content = _queuePanel, MaxHeight = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        // Pending files are always the first part of the image workspace.
        _imagePanel.Children.Insert(0, queueSection);
        var completedHeader = new Grid();
        completedHeader.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        completedHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _completedCount.VerticalAlignment = VerticalAlignment.Center;
        completedHeader.Children.Add(_completedCount);
        var clearFinished = ActionButton("Clear finished", () => { _queue.RemoveAll(item => item.Complete); UpdateQueue(); return Task.CompletedTask; });
        _busyDisabled.Add(clearFinished);
        Grid.SetColumn(clearFinished, 1);
        completedHeader.Children.Add(clearFinished);
        _completedSection.Children.Add(completedHeader);
        _completedSection.Children.Add(_emptyCompleted);
        _completedSection.Children.Add(new ScrollViewer { Content = _completedPanel, MaxHeight = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Name(_queuePanel, "Pending files");
        Name(_completedPanel, "Finished files");
        _actionPanel.Children.Add(_progress);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _convert.Background = Brush("Accent");
        _convert.Foreground = Brush("AccentInk");
        _convert.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        footer.Children.Add(_convert);
        footer.Children.Add(_cancel);
        footer.Children.Add(ActionButton("Open output folder", OpenOutputAsync));
        _actionPanel.Children.Add(footer);
    }

    private void SwitchMode(ConversionMode mode, bool compression = false)
    {
        _currentMode = mode;
        _compression = compression;
        var label = mode == ConversionMode.Pdf ? "PDF" : mode.ToString();
        var available = mode == ConversionMode.Images && !compression;
        var operation = compression ? "compression" : "conversion";
        _heading.Text = (compression ? "Compress " : "Convert ") + label.ToLowerInvariant();
        _subtitle.Text = label + " " + operation + " · " + (available ? "Local files, ready to go." : "Coming later.");
        _imagePanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        _completedSection.Visibility = _imagePanel.Visibility;
        _actionPanel.Visibility = _imagePanel.Visibility;
        _futurePanel.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        _futureTitle.Text = label + " " + operation + " is on the way";
        _futureDescription.Text = compression ? "Compression presets, file-size targets, and before/after comparisons will live here. Compression is not implemented yet." : mode switch
        {
            ConversionMode.Video => "A dedicated workspace for video formats, codecs, and export presets will live here.",
            ConversionMode.Audio => "Audio formats, bitrate controls, and batch exports will live here.",
            _ => "PDF conversion and document tools will live here. No document processing is enabled yet."
        };
        foreach (var (itemMode, button) in _modeButtons)
        {
            var selected = itemMode == (mode, compression);
            button.Background = selected ? Brush("Selected") : new SolidColorBrush(Colors.Transparent);
            AutomationProperties.SetItemStatus(button, selected ? "Current mode" : "");
        }
    }

    private async Task BrowseAsync()
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            InitializeWithWindow.Initialize(picker, _hwnd);
            foreach (var extension in ImageConversionEngine.InputExtensions.Order()) picker.FileTypeFilter.Add(extension);
            AddFiles((await picker.PickMultipleFilesAsync()).Select(file => file.Path));
        }
        catch (Exception ex) { Notify("Could not browse files", ex.Message, InfoBarSeverity.Error); }
    }

    private void AddFiles(IEnumerable<string> files)
    {
        var rejected = 0;
        foreach (var path in files)
        {
            if (!File.Exists(path) || !ImageConversionEngine.IsSupportedInput(path)) { rejected++; continue; }
            if (!_files.CanAdd(path))
            {
                if (_files.PendingCount >= FileQueue<QueueItem>.PendingLimit) rejected++;
                continue;
            }
            var item = new QueueItem(path);
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var details = new StackPanel { Spacing = 4 };
            details.Children.Add(Text(Path.GetFileName(path), 14));
            details.Children.Add(item.Status);
            row.Children.Add(details);
            var remove = ActionButton("Remove", () => { _queue.Remove(item); UpdateQueue(); return Task.CompletedTask; });
            Name(remove, "Remove " + Path.GetFileName(path));
            item.Remove = remove;
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            item.Row = Card(row);
            item.Row.Padding = new Thickness(14, 10, 14, 10);
            ToolTipService.SetToolTip(item.Row, path);
            _queue.Add(item);
        }
        UpdateQueue();
        if (rejected > 0) Notify("Some files were not added", $"Skipped {rejected} unsupported/missing files or files beyond the 500-item queue limit. Drop local raster images only.", InfoBarSeverity.Warning);
    }

    private void UpdateQueue()
    {
        _queuePanel.Children.Clear();
        _completedPanel.Children.Clear();
        foreach (var item in _queue) (item.Complete ? _completedPanel : _queuePanel).Children.Add(item.Row);
        var pending = _queue.Count(item => !item.Complete);
        var finished = _queue.Count - pending;
        _queueCount.Text = $"QUEUE · {pending}";
        _completedCount.Text = $"FINISHED · {finished}";
        _emptyQueue.Visibility = pending == 0 ? Visibility.Visible : Visibility.Collapsed;
        _emptyCompleted.Visibility = finished == 0 ? Visibility.Visible : Visibility.Collapsed;
        _convert.IsEnabled = _conversion == null && _queue.Any(item => !item.Complete);
    }

    private async Task ChooseFolderAsync()
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, _hwnd);
            if (await picker.PickSingleFolderAsync() is { } folder) { _output.Text = folder.Path; SavePreferences(); }
        }
        catch (Exception ex) { Notify("Could not choose a folder", ex.Message, InfoBarSeverity.Error); }
    }

    private async Task ConvertQueueAsync(QueueItem[]? requested = null, ImageFormat? requestedFormat = null)
    {
        if (_conversion != null) return;
        var format = requestedFormat ?? _format.SelectedItem as ImageFormat;
        if (format == null) return;
        var pending = requested ?? _queue.Where(item => !item.Complete).ToArray();
        if (pending.Length == 0) return;
        var options = new ConversionOptions(format, _output.Text, (uint)_quality.Value, _strip.IsChecked == true,
            _frames.SelectedIndex == 1 ? FramePolicy.FirstFrame : FramePolicy.Preserve);
        using var cancellation = new CancellationTokenSource();
        _conversion = cancellation;
        SetBusy(true);
        _notice.IsOpen = false;
        var succeeded = 0;
        var failed = 0;
        var attempted = 0;
        try
        {
            foreach (var item in pending)
            {
                if (cancellation.IsCancellationRequested) break;
                item.Status.Text = "Converting…";
                item.Status.Foreground = Brush("AccentText");
                try
                {
                    var result = await _engine.ConvertAsync(item.Path, options, cancellation.Token);
                    item.Complete = true;
                    item.Status.Text = $"Saved · {Path.GetFileName(result.Output)} · {FormatBytes(result.Bytes)}";
                    ToolTipService.SetToolTip(item.Status, result.Output);
                    succeeded++;
                    UpdateQueue();
                }
                catch (OperationCanceledException) { item.Status.Text = "Cancelled · ready to retry"; break; }
                catch (Exception ex)
                {
                    failed++;
                    item.Status.Text = "Failed · " + ex.Message;
                    item.Status.Foreground = Brush("Warning");
                    ToolTipService.SetToolTip(item.Status, ex.Message);
                }
                attempted++;
                _progress.Value = 100d * attempted / pending.Length;
            }
            Notify(cancellation.IsCancellationRequested ? "Conversion stopped" : "Batch complete",
                $"{succeeded} saved · {failed} failed · {pending.Length - attempted} not processed. Originals are untouched.",
                failed > 0 || cancellation.IsCancellationRequested ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        finally
        {
            _conversion = null;
            SetBusy(false);
            UpdateQueue();
            if (_exiting) Close();
        }
    }

    private void SetBusy(bool busy)
    {
        foreach (var control in _busyDisabled) control.IsEnabled = !busy;
        foreach (var item in _queue) item.Remove.IsEnabled = !busy;
        _convert.IsEnabled = !busy;
        _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _cancel.IsEnabled = true;
        _cancel.Content = "Cancel remaining";
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.Value = 0;
        UpdateQuality();
    }

    private void UpdateQuality()
    {
        var lossy = (_format.SelectedItem as ImageFormat)?.Extension is "jpg" or "webp" or "avif" or "jxl" or "jp2";
        _quality.IsEnabled = lossy && _conversion == null;
        _qualityValue.Text = lossy ? $"{(int)_quality.Value}%" : "Not applicable to this format";
    }

    private void SavePreferences()
    {
        if (!_ready) return;
        _settings.OutputExtension = (_format.SelectedItem as ImageFormat)?.Extension ?? "png";
        _settings.OutputFolder = _output.Text;
        _settings.Quality = (int)_quality.Value;
        _settings.StripMetadata = _strip.IsChecked == true;
        _settings.Frames = _frames.SelectedIndex == 1 ? FramePolicy.FirstFrame : FramePolicy.Preserve;
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify("Preferences not saved", ex.Message, InfoBarSeverity.Warning); }
    }

    private bool _settingsDialog;
    private async Task ShowSettingsAsync()
    {
        if (_settingsDialog || _cloudDialog) return;
        _settingsDialog = true;
        try { await ShowSettingsCoreAsync(); }
        catch (Exception ex) { Notify("Settings unavailable", ex.Message, InfoBarSeverity.Error); }
        finally { _settingsDialog = false; }
    }

    private async Task ShowSettingsCoreAsync()
    {
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(Text("Appearance", 18));
        var appearance = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        appearance.Items.Add("Dark");
        appearance.Items.Add("Light");
        appearance.SelectedIndex = _settings.LightMode ? 1 : 0;
        Name(appearance, "Appearance mode");
        panel.Children.Add(Field("LIGHT / DARK", appearance));
        var colorPicker = new GridView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false, MaxWidth = 430 };
        Name(colorPicker, "Accent color");
        foreach (var palette in ThemePalette.All)
        {
            var swatch = new StackPanel { Spacing = 6, Width = 100, Padding = new Thickness(8) };
            swatch.Children.Add(new Border { Background = new SolidColorBrush(ColorOf(palette.Accent)), Height = 26, CornerRadius = new CornerRadius(5) });
            swatch.Children.Add(Text(palette.Name, 13));
            var choice = new GridViewItem { Content = swatch, Tag = palette.Name };
            Name(choice, palette.Name + " color scheme");
            colorPicker.Items.Add(choice);
            if (palette.Name == _settings.AccentColor) colorPicker.SelectedItem = choice;
        }
        panel.Children.Add(Field("COLOR SCHEME", colorPicker));
        panel.Children.Add(Text("Windows context menu", 18));
        var contextMenu = new ToggleSwitch { Header = "Show Fluxa in the image context menu", IsOn = _settings.ContextMenuEnabled };
        panel.Children.Add(contextMenu);
        var contextFormat = new ComboBox { ItemsSource = ImageConversionEngine.OutputFormats, HorizontalAlignment = HorizontalAlignment.Stretch };
        contextFormat.SelectedItem = ImageConversionEngine.OutputFormats.First(f => f.Extension == _settings.ContextMenuExtension);
        Name(contextFormat, "Context menu output format");
        panel.Children.Add(Field("CONTEXT MENU CONVERTS TO", contextFormat));
        panel.Children.Add(Text("Convert uses this format and your current output folder, quality, metadata, and frame settings. Open in Fluxa only adds files to the queue. On Windows 11, use Show more options. Explorer supports these commands for selections of up to 15 images.", 13, "Muted"));
        var closeToTray = new ToggleSwitch { Header = "Keep running in the system tray when I close the window", IsOn = _settings.CloseToTray, IsEnabled = _tray != null };
        panel.Children.Add(closeToTray);
        panel.Children.Add(Text("Closing during a conversion keeps the job running. Use the tray menu to reopen any mode or Exit to stop after the current codec step.", 13, "Muted"));
        panel.Children.Add(Text("Supported local inputs", 16));
        panel.Children.Add(Text(string.Join(", ", ImageConversionEngine.InputExtensions.Order()), 13, "Muted"));
        panel.Children.Add(Text("Limits: 512 MB per input, 500 queued files, 256 frames, 32,768 pixels per dimension. Processing uses a bounded pixel cache. Cancellation takes effect between native codec steps.", 13, "Muted"));
        panel.Children.Add(Text("Cloud connections", 18));
        var googleId = new TextBox { Text = _settings.GoogleDriveClientId, PlaceholderText = "Desktop OAuth client ID" };
        var dropboxKey = new TextBox { Text = _settings.DropboxAppKey, PlaceholderText = "Dropbox app key" };
        Name(googleId, "Google Drive OAuth client ID"); Name(dropboxKey, "Dropbox app key");
        panel.Children.Add(Field("GOOGLE DRIVE CLIENT ID", googleId));
        panel.Children.Add(Field("DROPBOX APP KEY", dropboxKey));
        panel.Children.Add(Text("App identifiers are public, not passwords. Enable Google Drive API and create a Desktop OAuth client with drive.readonly permission. For Dropbox, create a Full Dropbox scoped app with files.metadata.read and files.content.read. Register this exact Dropbox redirect:", 13, "Muted"));
        var redirect = new TextBox { Text = CloudOAuth.DropboxRedirect, IsReadOnly = true };
        Name(redirect, "Dropbox OAuth redirect URI"); panel.Children.Add(redirect);
        panel.Children.Add(Text("Save, then use the import buttons to connect in your browser. Fluxa never uploads files. Saved tokens are encrypted for your Windows user. Changing an app identifier disconnects its local session; use Disconnect in the import browser to revoke access first.", 13, "Muted"));
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = "Settings & supported formats", Content = new ScrollViewer { Content = panel, MaxHeight = 480 }, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var contextExtension = ((ImageFormat)contextFormat.SelectedItem).Extension;
            try { ExplorerIntegration.Apply(contextMenu.IsOn, contextExtension); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { Notify("Explorer commands not saved", ex.Message, InfoBarSeverity.Error); return; }
            _settings.ContextMenuEnabled = contextMenu.IsOn;
            _settings.ContextMenuExtension = contextExtension;
            if (_settings.GoogleDriveClientId != googleId.Text.Trim()) { _cloudTokens.Delete(FileSourceKind.GoogleDrive); _cloudProviders.Remove(FileSourceKind.GoogleDrive); }
            if (_settings.DropboxAppKey != dropboxKey.Text.Trim()) { _cloudTokens.Delete(FileSourceKind.Dropbox); _cloudProviders.Remove(FileSourceKind.Dropbox); }
            _settings.GoogleDriveClientId = googleId.Text.Trim();
            _settings.DropboxAppKey = dropboxKey.Text.Trim();
            _settings.CloseToTray = closeToTray.IsOn;
            _settings.LightMode = appearance.SelectedIndex == 1;
            _settings.AccentColor = (colorPicker.SelectedItem as GridViewItem)?.Tag as string ?? "Blue";
            ApplyPalette();
            SwitchMode(_currentMode, _compression);
            SavePreferences();
        }
    }

    internal void ReceiveLaunch(LaunchRequest request)
    {
        OpenFromTray(ConversionMode.Images);
        _launches.Enqueue(request);
        if (!_handlingLaunches) _ = ProcessLaunchesAsync();
    }

    private async Task ProcessLaunchesAsync()
    {
        _handlingLaunches = true;
        try
        {
            while (_launches.TryDequeue(out var request))
            {
                // Never add to a batch while it is being enumerated; later Explorer
                // requests wait, without cancelling or accidentally converting manual imports.
                while ((_conversion != null || _cloudDialog || _settingsDialog) && !_exiting) await Task.Delay(100);
                if (_exiting) return;
                AddFiles(request.Files);
                if (request.ConvertImmediately)
                {
                    var paths = request.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var items = _queue.Where(item => !item.Complete && paths.Contains(item.Path)).ToArray();
                    var format = ImageConversionEngine.OutputFormats.First(f => f.Extension == _settings.ContextMenuExtension);
                    await ConvertQueueAsync(items, format);
                }
            }
        }
        catch (Exception ex) { Notify("Could not open Explorer files", ex.Message, InfoBarSeverity.Error); }
        finally { _handlingLaunches = false; }
    }

    private Task OpenOutputAsync()
    {
        try { Directory.CreateDirectory(_output.Text); Process.Start(new ProcessStartInfo(_output.Text) { UseShellExecute = true }); }
        catch (Exception ex) { Notify("Could not open output folder", ex.Message, InfoBarSeverity.Error); }
        return Task.CompletedTask;
    }

    private void OpenFromTray(ConversionMode? mode)
    {
        AppWindow.Show();
        Activate();
        TrayIcon.Restore(_hwnd);
        if (mode.HasValue) SwitchMode(mode.Value);
    }

    private void Exit()
    {
        _exiting = true;
        if (_conversion != null) { _conversion.Cancel(); return; }
        Close();
    }

    private void Notify(string title, string message, InfoBarSeverity severity)
    {
        _notice.Title = title;
        _notice.Message = message;
        _notice.Severity = severity;
        _notice.IsOpen = true;
    }

    private static void ConfigureSidebarButton(Button button)
    {
        button.Style = (Style)Application.Current.Resources["SidebarButtonStyle"];
        // Explicit local brushes avoid stock animation and template-resource state caching.
        button.PointerEntered += (_, _) => button.Background = Brush("NavHover");
        button.PointerPressed += (_, _) => button.Background = Brush("NavPressed");
        button.PointerReleased += (_, _) => button.Background = Brush("NavHover");
        button.PointerExited += (_, _) => button.Background = AutomationProperties.GetItemStatus(button) == "Current mode"
            ? Brush("Selected") : new SolidColorBrush(Colors.Transparent);
    }

    private static Button ActionButton(string text, Func<Task> action)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) => await action();
        return button;
    }
    private static StackPanel Field(string label, UIElement control)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Text(label, 11, "Muted"));
        panel.Children.Add(control);
        return panel;
    }
    private static Border Card(UIElement content) => new()
    {
        Background = Brush("Surface"), CornerRadius = new CornerRadius(12), Padding = new Thickness(20),
        BorderBrush = Brush("Border"), BorderThickness = new Thickness(1), Child = content
    };
    private static TextBlock Text(string value, double size, string color = "Text") => new()
    {
        Text = value, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap,
        FontWeight = size >= 18 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
    };
    private static Color ColorOf(string hex) => Color.FromArgb(255, Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
    private static SolidColorBrush Brush(string key)
    {
        if (!ThemeBrushes.TryGetValue(key, out var brush)) ThemeBrushes[key] = brush = new SolidColorBrush(Colors.Transparent);
        return brush;
    }
    private static void Name(DependencyObject element, string name) => AutomationProperties.SetName(element, name);
    private static string FormatBytes(long bytes) => bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes / (1024d * 1024):0.#} MB";

    private sealed class QueueItem(string path)
    {
        public string Path { get; } = path;
        public bool Complete { get; set; }
        public TextBlock Status { get; } = Text("Ready", 12, "Muted");
        public Border Row { get; set; } = null!;
        public Button Remove { get; set; } = null!;
    }
}
