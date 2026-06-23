using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ICSharpCode.ILSpy.TreeNodes;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ICSharpCode.ILSpy.Controls;

using ILSpyResources = ICSharpCode.ILSpy.Properties.Resources;

public sealed class ResourceStringTable : UserControl
{
    public ResourceStringTable(IEnumerable strings, FrameworkElement container)
    {
        Content = new ResourceTableView<ResourceStringRow>(
            strings.Cast<KeyValuePair<string, string>>().Select(ResourceStringRow.From),
            container,
            ILSpyResources.StringTable,
            [
                new ResourceColumn<ResourceStringRow>(ILSpyResources.Name, nameof(ResourceStringRow.Key)),
                new ResourceColumn<ResourceStringRow>(ILSpyResources.Value, nameof(ResourceStringRow.Value)),
            ],
            row => row.Key,
            row => row.Value,
            row => $"{row.Key}\t{row.Value}");
    }
}

public sealed class ResourceObjectTable : UserControl
{
    public ResourceObjectTable(IEnumerable resources, FrameworkElement container)
    {
        Content = new ResourceTableView<ResourceObjectRow>(
            resources.Cast<ResourcesFileTreeNode.SerializedObjectRepresentation>().Select(ResourceObjectRow.From),
            container,
            ILSpyResources.OtherResources,
            [
                new ResourceColumn<ResourceObjectRow>(ILSpyResources.Name, nameof(ResourceObjectRow.Key)),
                new ResourceColumn<ResourceObjectRow>(ILSpyResources.ValueString, nameof(ResourceObjectRow.Value)),
                new ResourceColumn<ResourceObjectRow>(ILSpyResources.Type, nameof(ResourceObjectRow.Type)),
            ],
            row => row.Key,
            row => $"{row.Value} {row.Type}",
            row => $"{row.Key}\t{row.Value}\t{row.Type}");
    }

    private sealed record ResourceObjectRow(string Key, string Value, string Type)
    {
        public static ResourceObjectRow From(ResourcesFileTreeNode.SerializedObjectRepresentation item) =>
            new(item.Key, item.Value, item.Type);
    }
}

internal sealed class ResourceTableView<TRow> : UserControl
{
    private readonly List<TRow> allRows;
    private readonly Func<TRow, string> keySelector;
    private readonly Func<TRow, string> searchSelector;
    private readonly IReadOnlyList<ResourceColumn<TRow>> columns;
    private readonly StackPanel rowsPanel;
    private readonly TextBox filterBox;

    public ResourceTableView(
        IEnumerable<TRow> rows,
        FrameworkElement container,
        string title,
        IReadOnlyList<ResourceColumn<TRow>> columns,
        Func<TRow, string> keySelector,
        Func<TRow, string> searchSelector,
        Func<TRow, string> copySelector)
    {
        allRows = rows.ToList();
        this.keySelector = keySelector;
        this.searchSelector = searchSelector;
        this.columns = columns;

        var titleBlock = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = title,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI"),
            FontSize = 16,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
            Margin = new Thickness(0, 0, 0, 6),
        };

        filterBox = new TextBox
        {
            PlaceholderText = "Search",
            MinWidth = 240,
            Margin = new Thickness(0, 0, 0, 6),
        };
        filterBox.TextChanged += (_, _) => ApplyFilter();

        rowsPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
        };

        var table = new Grid();
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = CreateRowGrid();
        header.Background = new SolidColorBrush(Color.FromArgb(255, 245, 245, 245));
        for (var i = 0; i < columns.Count; i++)
        {
            var text = CreateCell(columns[i].Header, bold: true);
            Grid.SetColumn(text, i);
            header.Children.Add(text);
        }

        var scrollViewer = new ScrollViewer
        {
            Content = rowsPanel,
            Height = 140,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(scrollViewer, 1);
        table.Children.Add(header);
        table.Children.Add(scrollViewer);

        var tableBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 172, 172, 172)),
            BorderThickness = new Thickness(1),
            Child = table,
        };

        var searchBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 172, 172, 172)),
            BorderThickness = new Thickness(1),
            Child = filterBox,
            Margin = new Thickness(0, 0, 0, 0),
        };

        var panel = new Grid { Margin = new Thickness(5, 0, 0, 0) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(titleBlock, 0);
        Grid.SetRow(searchBorder, 1);
        Grid.SetRow(tableBorder, 2);
        panel.Children.Add(titleBlock);
        panel.Children.Add(searchBorder);
        panel.Children.Add(tableBorder);

        Content = panel;
        Width = GetInitialWidth(container);
        MaxHeight = 260;
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        ApplyFilter();
    }

    private Grid CreateRowGrid()
    {
        var row = new Grid { MinHeight = 20 };
        foreach (var _ in columns)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return row;
    }

    private static TextBlock CreateCell(string text, bool bold = false) =>
        new()
        {
            Text = text,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI"),
            FontSize = 12,
            FontWeight = bold ? new Windows.UI.Text.FontWeight { Weight = 700 } : new Windows.UI.Text.FontWeight { Weight = 400 },
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0)),
            Padding = new Thickness(4, 1, 4, 1),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

    private void PopulateRows(IEnumerable<TRow> rows)
    {
        rowsPanel.Children.Clear();
        var index = 0;
        foreach (var row in rows)
        {
            var gridRow = CreateRowGrid();
            gridRow.Background = index % 2 == 1
                ? new SolidColorBrush(Color.FromArgb(38, 204, 204, 51))
                : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

            for (var i = 0; i < columns.Count; i++)
            {
                var text = CreateCell(GetPropertyText(row, columns[i].PropertyName));
                Grid.SetColumn(text, i);
                gridRow.Children.Add(text);
            }

            rowsPanel.Children.Add(gridRow);
            index++;
        }
    }

    private static string GetPropertyText(TRow row, string propertyName)
    {
        var property = typeof(TRow).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        return property?.GetValue(row)?.ToString() ?? string.Empty;
    }

    private void ApplyFilter()
    {
        var filter = filterBox.Text?.Trim();
        if (string.IsNullOrEmpty(filter))
        {
            PopulateRows(allRows);
            return;
        }

        PopulateRows(allRows
            .Where(row =>
                Contains(keySelector(row), filter) ||
                Contains(searchSelector(row), filter))
            .ToList());
    }

    private static double GetInitialWidth(FrameworkElement container)
    {
        var width = container.ActualWidth;
        if (double.IsNaN(width) || width <= 0)
            width = container.Width;
        if (double.IsNaN(width) || width <= 0)
            width = 520;
        return Math.Max(width - 45, 240);
    }

    private static bool Contains(string? value, string filter) =>
        value?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
}

internal sealed record ResourceColumn<TRow>(string Header, string PropertyName);

internal sealed record ResourceStringRow(string Key, string Value)
{
    public static ResourceStringRow From(KeyValuePair<string, string> item) => new(item.Key, item.Value);
}
