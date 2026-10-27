using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace EnhanceCapCut.App;

// Building blocks that reproduce the macOS app's SwiftUI `Form` in the `.grouped` style: titled sections drawn as
// rounded cards, one row per setting with its label on the left and the control on the right, hairlines between rows,
// a caption under the section. The windows are built from them in code (no XAML markup), so both platforms show the
// same layout, texts and order.

/// <summary>Colours and icons shared by the windows.</summary>
static class Theme
{
    // The macOS system colours the windows use for status (systemGreen, systemOrange, systemRed) and the Donate heart
    // (systemPink).
    public static readonly SolidColorBrush Green = new(Color.FromArgb(255, 0x34, 0xC7, 0x59));
    public static readonly SolidColorBrush Orange = new(Color.FromArgb(255, 0xFF, 0x95, 0x00));
    public static readonly SolidColorBrush Red = new(Color.FromArgb(255, 0xFF, 0x3B, 0x30));
    public static readonly SolidColorBrush Pink = new(Color.FromArgb(255, 0xFF, 0x2D, 0x55));

    public static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
    public static Brush Primary => Resource("TextFillColorPrimaryBrush");
    public static Brush Secondary => Resource("TextFillColorSecondaryBrush");

    public static Color Accent => Application.Current.Resources.TryGetValue("SystemAccentColor", out var c) && c is Color color
        ? color : Color.FromArgb(255, 0x00, 0x67, 0xC0);

    public static SolidColorBrush AccentTint(double opacity) => new(Accent) { Opacity = opacity };
    public static SolidColorBrush GrayTint(double opacity) => new(Colors.Gray) { Opacity = opacity };

    // Segoe Fluent Icons (Segoe MDL2 Assets on Windows 10) in place of the SF Symbols of the macOS version.
    public const string Refresh = "";
    public const string Warning = "";
    public const string CheckFilled = "";
    public const string ErrorFilled = "";
    public const string Circle = "";
    public const string Download = "";
    public const string Speaker = "";
    public const string Captions = "";
    public const string Waveform = "";
    public const string Up = "";
    public const string HeartFill = "\uEB52";
    public const string Globe = "\uE774";
    public const string Down = "";

    public static FontIcon Icon(string glyph, double size = 14, Brush? foreground = null)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = size };
        if (foreground is not null) icon.Foreground = foreground;
        return icon;
    }
}

/// <summary>Text helpers in the macOS text styles (body, callout, caption, headline).</summary>
static class Text
{
    public static TextBlock Body(string text = "") => new() { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    public static TextBlock Callout(string text = "") => new()
    {
        Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Caption(string text = "", Brush? foreground = null) => new()
    {
        Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = foreground ?? Theme.Secondary,
    };

    public static TextBlock Headline(string text = "") => new() { Text = text, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };

    /// <summary>A one-line text that ends in "…" when it doesn't fit.</summary>
    public static TextBlock Line(string text = "", double size = 14) => new()
    {
        Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Icon and text side by side, like SwiftUI's <c>Label</c>; the text wraps or trims in the width left.</summary>
    public static Grid Label(string glyph, TextBlock text, Brush? iconColor = null)
    {
        var icon = Theme.Icon(glyph, 14, iconColor ?? text.Foreground);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 3, 0, 0);
        return IconAndText(icon, text, 6);
    }

    /// <summary>An icon column and a text column that takes the rest of the width.</summary>
    public static Grid IconAndText(FrameworkElement icon, FrameworkElement text, double spacing)
    {
        var grid = new Grid { ColumnSpacing = spacing };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        return grid;
    }
}

/// <summary>The scrolling column of sections.</summary>
sealed class FormView : UserControl
{
    readonly StackPanel column = new() { Spacing = 18, Padding = new Thickness(20, 16, 20, 20) };
    readonly ContentControl holder;
    readonly List<FormSection> sections = [];

    public FormView()
    {
        holder = new ContentControl
        {
            Content = column, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsTabStop = false,
        };
        Content = new ScrollViewer
        {
            Content = holder, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    /// <summary>Greys out every control of the form (SwiftUI's <c>.disabled</c>) while it still scrolls.</summary>
    public bool IsFormEnabled
    {
        get => holder.IsEnabled;
        set
        {
            holder.IsEnabled = value;
            column.Opacity = value ? 1 : 0.6;
        }
    }

    public FormSection Section(string? title = null)
    {
        var section = new FormSection(title);
        sections.Add(section);
        column.Children.Add(section);
        return section;
    }

    /// <summary>Recomputes the hairlines and hides sections without visible rows; call after changing row visibility.</summary>
    public void Refresh()
    {
        foreach (var section in sections) section.Refresh();
    }
}

/// <summary>A titled card of rows with an optional caption under it.</summary>
sealed class FormSection : StackPanel
{
    readonly StackPanel card = new();
    readonly Border cardBorder;
    readonly List<FormRow> rows = [];
    readonly ContentPresenter header = new() { Margin = new Thickness(4, 0, 4, 6) };
    readonly TextBlock footer = Text.Caption();

    /// <summary>When false the section is hidden whatever its rows say (SwiftUI's conditional sections).</summary>
    public bool IsShown { get; set; } = true;

    public FormSection(string? title)
    {
        cardBorder = new Border
        {
            Child = card, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Background = Theme.Resource("CardBackgroundFillColorDefaultBrush"), BorderBrush = Theme.Resource("CardStrokeColorDefaultBrush"),
        };
        footer.Margin = new Thickness(4, 6, 4, 0);
        footer.Visibility = Visibility.Collapsed;
        if (title is not null) Header = Text.Headline(title);
        else header.Visibility = Visibility.Collapsed;
        Children.Add(header);
        Children.Add(cardBorder);
        Children.Add(footer);
    }

    public UIElement? Header
    {
        get => header.Content as UIElement;
        set
        {
            header.Content = value;
            header.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>The caption under the card; null or empty hides it.</summary>
    public string? Footer
    {
        get => footer.Text;
        set
        {
            footer.Text = value ?? "";
            footer.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>A caption with other content (a link button) instead of plain text.</summary>
    public void FooterContent(UIElement content)
    {
        footer.Visibility = Visibility.Collapsed;
        Children.Add(new ContentPresenter { Content = content, Margin = new Thickness(4, 6, 4, 0) });
    }

    public FormRow Add(FormRow row)
    {
        rows.Add(row);
        card.Children.Add(row);
        return row;
    }

    public FormRow Add(UIElement content) => Add(new FormRow(content));

    /// <summary>Removes every row (for sections whose rows come from data).</summary>
    public void Clear()
    {
        foreach (var row in rows) row.Release();
        rows.Clear();
        card.Children.Clear();
    }
    public FormRow Add(string label, UIElement control, bool stretch = false) => Add(FormRow.Labeled(label, control, stretch));

    public void Refresh()
    {
        var first = true;
        foreach (var row in rows)
        {
            if (!row.IsShown) continue;
            row.ShowSeparator(!first);
            first = false;
        }
        Visibility = IsShown && !first ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>One row of a section; <see cref="IsShown"/> hides it (the section redraws its hairlines on refresh).</summary>
sealed class FormRow : Grid
{
    readonly Rectangle separator = new()
    {
        Height = 1, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0),
        Fill = Theme.Resource("DividerStrokeColorDefaultBrush"),
    };

    readonly ContentPresenter holder;

    public FormRow(UIElement content)
    {
        MinHeight = 44;
        holder = new ContentPresenter
        {
            Content = content, Padding = new Thickness(12, 6, 12, 6), VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        Children.Add(holder);
        Children.Add(separator);
    }

    /// <summary>The row's label, for rows made with <see cref="Labeled"/>.</summary>
    public TextBlock? Label { get; private init; }

    /// <summary>Label on the left, <paramref name="control"/> on the right (or filling the rest with <paramref name="stretch"/>).</summary>
    public static FormRow Labeled(string label, UIElement control, bool stretch = false)
    {
        var text = Text.Body(label);
        text.Margin = new Thickness(0, 0, 16, 0);
        var grid = new Grid { ColumnSpacing = 0 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = stretch ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = stretch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            if (!stretch) fe.HorizontalAlignment = HorizontalAlignment.Right;
        }
        SetColumn((FrameworkElement)control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return new FormRow(grid) { Label = text };
    }

    public bool IsShown
    {
        get => Visibility == Visibility.Visible;
        set => Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Lets go of the row's controls, so a section rebuilt from data can put the same controls into new rows.</summary>
    public void Release()
    {
        if (holder.Content is Grid { } grid && Label is not null) grid.Children.Clear();
        holder.Content = null;
    }

    public void ShowSeparator(bool show) => separator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>A pop-up menu of values (SwiftUI's <c>Picker</c>), rebuilt only when its items change.</summary>
sealed class Picker<T> : ComboBox
{
    List<T> values = [];
    string signature = "";
    bool updating;

    public event Action<T>? Picked;

    public Picker()
    {
        MinWidth = 120;
        MaxWidth = 340;
        SelectionChanged += (_, _) =>
        {
            if (!updating && SelectedIndex >= 0 && SelectedIndex < values.Count) Picked?.Invoke(values[SelectedIndex]);
        };
    }

    public void Set(IEnumerable<(T Value, string Title)> items, T? selected)
    {
        updating = true;
        try
        {
            var list = items.ToList();
            var newSignature = string.Join("\n", list.Select(i => i.Title + "\t" + i.Value));
            if (newSignature != signature)
            {
                signature = newSignature;
                values = list.Select(i => i.Value).ToList();
                Items.Clear();
                foreach (var item in list) Items.Add(item.Title);
            }
            SelectedIndex = values.FindIndex(v => EqualityComparer<T>.Default.Equals(v, selected));
        }
        finally
        {
            updating = false;
        }
    }
}

/// <summary>A switch with no text of its own (SwiftUI's <c>Toggle</c> in a grouped form: the label is the row's).</summary>
sealed class Switch : UserControl
{
    // The template keeps a gap for the On/Off text after the knob: pulled back so the knob lines up with the other controls.
    readonly ToggleSwitch toggle = new() { OnContent = "", OffContent = "", MinWidth = 0, Margin = new Thickness(0, 0, -12, 0) };
    bool updating;
    public event Action<bool>? Switched;

    public Switch()
    {
        Content = toggle;
        toggle.Toggled += (_, _) => { if (!updating) Switched?.Invoke(toggle.IsOn); };
    }

    public void Set(bool on)
    {
        updating = true;
        toggle.IsOn = on;
        updating = false;
    }
}

/// <summary>A segmented control (SwiftUI's <c>.pickerStyle(.segmented)</c>): a track with the chosen segment raised.</summary>
sealed class Segmented<T> : UserControl
{
    readonly Grid segments = new();
    readonly List<(T Value, Border View)> items = [];
    T? selected;

    public event Action<T>? Picked;

    public Segmented(IEnumerable<(T Value, string Title)> options)
    {
        Content = new Border { Child = segments, CornerRadius = new CornerRadius(6), Padding = new Thickness(2), Background = Theme.GrayTint(0.16) };
        foreach (var (value, title) in options)
        {
            var view = new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 4, 12, 4), MinWidth = 64, BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = title, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            view.Tapped += (_, _) =>
            {
                if (!IsEnabledInTree(this)) return;
                Select(value);
                Picked?.Invoke(value);
            };
            segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(view, items.Count);
            segments.Children.Add(view);
            items.Add((value, view));
        }
        Select(items.Count > 0 ? items[0].Value : default);
    }

    public void Select(T? value)
    {
        selected = value;
        foreach (var (v, view) in items)
        {
            var on = EqualityComparer<T>.Default.Equals(v, selected);
            view.Background = on ? Theme.Resource("ControlFillColorDefaultBrush") : new SolidColorBrush(Colors.Transparent);
            view.BorderBrush = on ? Theme.Resource("ControlStrokeColorDefaultBrush") : new SolidColorBrush(Colors.Transparent);
        }
    }

    /// <summary>Custom controls built from borders don't grey out by themselves: they check their enclosing control.</summary>
    internal static bool IsEnabledInTree(DependencyObject element)
    {
        for (var e = element; e is not null; e = VisualTreeHelper.GetParent(e))
            if (e is Control { IsEnabled: false }) return false;
        return true;
    }
}

/// <summary>A selectable language tile: check circle, name and native name (the macOS window's language chips).</summary>
sealed class Chip : UserControl
{
    readonly FontIcon icon = Theme.Icon(Theme.Circle, 16);
    readonly Border box = new() { CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 5, 8, 5) };
    bool isOn;

    public Chip(string title, string? subtitle, string tooltip, Action toggle)
    {
        Content = box;
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Text.Line(title, 13));
        if (subtitle is not null)
        {
            texts.Children.Add(new TextBlock
            {
                Text = subtitle, FontSize = 10, Foreground = Theme.Secondary, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            });
        }
        icon.VerticalAlignment = VerticalAlignment.Center;
        box.Child = Text.IconAndText(icon, texts, 6);
        ToolTipService.SetToolTip(this, tooltip);
        Tapped += (_, _) => { if (Segmented<int>.IsEnabledInTree(this)) toggle(); };
        PointerEntered += (_, _) => Opacity = 0.85;
        PointerExited += (_, _) => Opacity = 1;
        IsOn = false;
    }

    public bool IsOn
    {
        get => isOn;
        set
        {
            isOn = value;
            icon.Glyph = value ? Theme.CheckFilled : Theme.Circle;
            icon.Foreground = value ? new SolidColorBrush(Theme.Accent) : Theme.Secondary;
            box.Background = value ? Theme.AccentTint(0.15) : Theme.GrayTint(0.08);
        }
    }
}

/// <summary>A wide on/off tile with an icon (the macOS window's "Subtitles" / "Voice-over" buttons).</summary>
sealed class OutputToggle : UserControl
{
    readonly Border box = new() { CornerRadius = new CornerRadius(7), Padding = new Thickness(0, 6, 0, 6) };
    readonly string glyph;
    readonly FontIcon icon;
    readonly TextBlock title;
    bool isOn;

    public OutputToggle(string text, string glyph, Action toggle)
    {
        this.glyph = glyph;
        icon = Theme.Icon(glyph, 15);
        title = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Content = box;
        box.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Children = { icon, title },
        };
        Tapped += (_, _) => { if (Segmented<int>.IsEnabledInTree(this)) toggle(); };
        PointerEntered += (_, _) => Opacity = 0.85;
        PointerExited += (_, _) => Opacity = 1;
        IsOn = false;
    }

    public bool IsOn
    {
        get => isOn;
        set
        {
            isOn = value;
            icon.Glyph = value ? Theme.CheckFilled : glyph;
            var foreground = value ? new SolidColorBrush(Theme.Accent) : Theme.Primary;
            icon.Foreground = foreground;
            title.Foreground = foreground;
            box.Background = value ? Theme.AccentTint(0.18) : Theme.GrayTint(0.08);
        }
    }
}

/// <summary>A small up/down control next to a number field (SwiftUI's <c>Stepper</c>).</summary>
sealed class Stepper : StackPanel
{
    public Stepper(Action up, Action down)
    {
        VerticalAlignment = VerticalAlignment.Center;
        foreach (var (glyph, action) in new[] { (Theme.Up, up), (Theme.Down, down) })
        {
            var button = new RepeatButton
            {
                Content = Theme.Icon(glyph, 8), Padding = new Thickness(6, 1, 6, 1), MinHeight = 0, Height = 15,
                CornerRadius = new CornerRadius(3),
            };
            button.Click += (_, _) => action();
            Children.Add(button);
        }
    }
}

/// <summary>Lays children out in as many equal columns of at least <see cref="MinItemWidth"/> as fit (SwiftUI's adaptive grid).</summary>
sealed class AdaptiveGrid : Panel
{
    public double MinItemWidth { get; init; } = 110;
    public double Spacing { get; init; } = 6;

    int Columns(double width) => double.IsInfinity(width) ? Math.Max(1, Children.Count)
        : Math.Max(1, (int)((width + Spacing) / (MinItemWidth + Spacing)));

    protected override Size MeasureOverride(Size available)
    {
        var columns = Columns(available.Width);
        var itemWidth = double.IsInfinity(available.Width) ? MinItemWidth : (available.Width - Spacing * (columns - 1)) / columns;
        double height = 0, rowHeight = 0;
        var visible = Children.Where(c => c.Visibility == Visibility.Visible).ToList();
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, visible[i].DesiredSize.Height);
            if (i % columns == columns - 1 || i == visible.Count - 1)
            {
                height += rowHeight + (i == visible.Count - 1 ? 0 : Spacing);
                rowHeight = 0;
            }
        }
        var width = double.IsInfinity(available.Width) ? columns * (MinItemWidth + Spacing) - Spacing : available.Width;
        return new Size(Math.Max(0, width), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var columns = Columns(final.Width);
        var itemWidth = (final.Width - Spacing * (columns - 1)) / columns;
        var visible = Children.Where(c => c.Visibility == Visibility.Visible).ToList();
        double y = 0;
        for (var start = 0; start < visible.Count; start += columns)
        {
            var row = visible.Skip(start).Take(columns).ToList();
            var rowHeight = row.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < row.Count; i++) row[i].Arrange(new Rect(i * (itemWidth + Spacing), y, itemWidth, rowHeight));
            y += rowHeight + Spacing;
        }
        return final;
    }
}

/// <summary>The bar under the form: a hairline on top, then the status and the window's main button.</summary>
sealed class FooterBar : Grid
{
    readonly ContentPresenter content = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };

    public FooterBar()
    {
        Children.Add(new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Top, Fill = Theme.Resource("DividerStrokeColorDefaultBrush") });
        Children.Add(content);
        content.Margin = new Thickness(16);
    }

    public UIElement? Content
    {
        get => content.Content as UIElement;
        set => content.Content = value;
    }
}
