using System.Windows;
using System.Windows.Controls;

namespace PasteJump.App.Views;

/// <summary>One setting the search box can find.</summary>
/// <param name="Label">The label as it reads on the tab, which is what the user will have searched for.</param>
/// <param name="TabName">Which tab it lives on, shown beside the label so the answer teaches where things are.</param>
/// <param name="Tab">The tab to select.</param>
/// <param name="Target">The control to scroll to and highlight - the row itself when there is no single control.</param>
/// <param name="SearchText">
/// Label, tab name and the inline help beneath the control, lower-cased once at build time. The help is included
/// because it holds the words people actually search for: "Excel" appears only in the paste-delay explanation,
/// and "cache" nowhere else at all.
/// </param>
internal sealed record SettingsSearchHit(
    string Label,
    string TabName,
    TabItem Tab,
    FrameworkElement Target,
    string SearchText);

/// <summary>
/// Builds the settings dialog's search index by reading the dialog itself.
/// <para>
/// Read from the live controls rather than from a hand-written table, for the reason the Advanced tab already
/// exists: a list of settings maintained by hand drifts the first time someone forgets it, and a search that
/// silently cannot find a setting invites the conclusion that the setting does not exist. This way a new row in
/// the XAML is searchable the moment it is added, with nothing to remember.
/// </para>
/// <para>
/// <b>The logical tree, not the visual one.</b> A <see cref="TabControl"/> applies the template for the selected
/// tab only, so a visual-tree walk would find the first tab's controls and nothing else - and the search would
/// quietly cover one tab in eight. The content of every <see cref="TabItem"/> is nevertheless constructed when
/// the XAML is parsed, so <see cref="LogicalTreeHelper"/> reaches all of it without selecting anything.
/// </para>
/// </summary>
internal static class SettingsSearch
{
    /// <summary>
    /// Indexes every labelled row and check box across all tabs.
    /// <para>
    /// Two shapes are recognised, which is all the dialog uses: a <c>SettingRow</c> grid holding a label and an
    /// editor, and a bare <see cref="CheckBox"/> whose content is its own label. Section headings and prose are
    /// skipped - they are not settings - except that an inline help line attaches to whatever row preceded it,
    /// which is what makes the help text searchable.
    /// </para>
    /// </summary>
    /// <param name="settingRowStyle">
    /// The <c>SettingRow</c> style, passed in rather than resolved here. Identifying a row by its style is exact,
    /// where a structural guess ("a grid with a label and a control") would quietly match layout grids too - but
    /// resolving a resource from an element whose tab has never been selected is not something to rely on, and the
    /// window owns the dictionary anyway.
    /// </param>
    public static List<SettingsSearchHit> Build(TabControl tabs, Style settingRowStyle)
    {
        ArgumentNullException.ThrowIfNull(tabs);

        var hits = new List<SettingsSearchHit>();

        foreach (var tab in tabs.Items.OfType<TabItem>())
        {
            var tabName = HeaderText(tab);

            // Flattened in document order, so a help line can be attached to the row above it.
            var flat = new List<FrameworkElement>();
            Flatten(tab.Content as DependencyObject, flat);

            string? currentLabel = null;
            FrameworkElement? currentTarget = null;
            var text = new System.Text.StringBuilder();

            void Commit()
            {
                if (currentLabel is { Length: > 0 } && currentTarget is not null)
                {
                    hits.Add(new SettingsSearchHit(
                        currentLabel,
                        tabName,
                        tab,
                        currentTarget,
                        text.ToString().ToLowerInvariant()));
                }

                currentLabel = null;
                currentTarget = null;
                text.Clear();
            }

            foreach (var element in flat)
            {
                switch (element)
                {
                    case CheckBox { Content: string content } check when content.Length > 0:
                        Commit();
                        currentLabel = content;
                        currentTarget = check;
                        text.Append(content).Append(' ').Append(tabName);
                        break;

                    case Grid grid when ReferenceEquals(grid.Style, settingRowStyle):
                    {
                        var label = grid.Children.OfType<TextBlock>().FirstOrDefault()?.Text;

                        if (string.IsNullOrWhiteSpace(label))
                        {
                            break;
                        }

                        Commit();
                        currentLabel = label;

                        // The editor if there is one, else the row. Either scrolls into view; the row is the
                        // better highlight target anyway, but the control is what the user wants to reach.
                        currentTarget = grid.Children
                            .OfType<FrameworkElement>()
                            .FirstOrDefault(static c => c is not TextBlock) ?? grid;

                        text.Append(label).Append(' ').Append(tabName);
                        break;
                    }

                    // Inline help, which belongs to the row above it.
                    case TextBlock block when currentLabel is not null && block.Text.Length > 0:
                        text.Append(' ').Append(block.Text);
                        break;
                }
            }

            Commit();
        }

        return hits;
    }

    /// <summary>Matches on any whitespace-separated word, all of which must appear. Empty query matches nothing.</summary>
    public static List<SettingsSearchHit> Filter(IReadOnlyList<SettingsSearchHit> all, string? query)
    {
        ArgumentNullException.ThrowIfNull(all);

        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
        {
            return [];
        }

        return
        [
            .. all
                .Where(h => words.All(w => h.SearchText.Contains(w.ToLowerInvariant(), StringComparison.Ordinal)))

                // A label match first, then the rest. Someone typing "theme" wants the Theme row, not the three
                // other settings whose help text happens to mention it.
                .OrderByDescending(h => words.All(w => h.Label.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(static h => h.Label, StringComparer.CurrentCultureIgnoreCase)
        ];
    }

    private static string HeaderText(TabItem tab)
        => (tab.Header as string ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Every <see cref="FrameworkElement"/> beneath a node, in document order.
    /// <para>
    /// <see cref="LogicalTreeHelper"/> rather than the visual tree, which is the whole point - see the note on
    /// this class. It also means no layout has happened, so nothing here may ask about sizes or positions.
    /// </para>
    /// </summary>
    private static void Flatten(DependencyObject? node, List<FrameworkElement> into)
    {
        if (node is null)
        {
            return;
        }

        if (node is FrameworkElement element)
        {
            into.Add(element);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject dependencyObject)
            {
                Flatten(dependencyObject, into);
            }
        }
    }
}
