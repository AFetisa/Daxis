using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using Daxis.Core;

namespace Daxis.App;

public static class Editors
{
    static readonly Dictionary<string, IHighlightingDefinition> Cache = [];

    // Per-editor language so one editor can switch between DAX and M.
    sealed class State { public string Language = "DAX"; public Action Apply = () => { }; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextEditor, State> States = new();

    public static void SetLanguage(TextEditor editor, string language)
    {
        var st = States.GetOrCreateValue(editor);
        if (st.Language == language) return;
        st.Language = language;
        st.Apply();
    }

    static IHighlightingDefinition Definition(string name)
    {
        if (Cache.TryGetValue(name, out var d)) return d;
        using var s = typeof(Editors).Assembly.GetManifestResourceStream($"Daxis.App.Highlighting.{name}.xshd")!;
        using var r = XmlReader.Create(s);
        return Cache[name] = HighlightingLoader.Load(r, HighlightingManager.Instance);
    }

    /// <summary>Applies shared options and keeps syntax colours in step with the app theme.</summary>
    public static void Configure(TextEditor editor, string language)
    {
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 4;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.HighlightCurrentLine = true;
        if (!editor.IsSet(TextEditor.ShowLineNumbersProperty)) editor.ShowLineNumbers = true;
        foreach (var m in editor.TextArea.LeftMargins)
            if (m is AvaloniaEdit.Editing.LineNumberMargin ln) ln.Margin = new Thickness(14, 0, 10, 0);
            else if (m is Avalonia.Controls.Shapes.Shape line) line.IsVisible = false;

        var app = Application.Current!;
        var state = States.GetOrCreateValue(editor);
        state.Language = language;
        void Apply(object? _ = null, EventArgs? __ = null)
        {
            var dark = app.ActualThemeVariant == ThemeVariant.Dark;
            editor.SyntaxHighlighting = Definition($"{state.Language}-{(dark ? "Dark" : "Light")}");
            if (app.TryGetResource("EditorLine", app.ActualThemeVariant, out var line) && line is IBrush lb)
            {
                editor.TextArea.TextView.CurrentLineBackground = lb;
                editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent);
            }
            if (app.TryGetResource("EditorSelection", app.ActualThemeVariant, out var sel) && sel is IBrush sb)
            {
                editor.TextArea.SelectionBrush = sb;
                editor.TextArea.SelectionBorder = null;
            }
        }
        state.Apply = () => Apply();
        Apply();
        // Views are cached and re-attached on tab switches: follow the theme only while visible, catch up on return.
        app.ActualThemeVariantChanged += Apply;
        editor.DetachedFromVisualTree += (_, _) => app.ActualThemeVariantChanged -= Apply;
        editor.AttachedToVisualTree += (_, _) =>
        {
            app.ActualThemeVariantChanged -= Apply;
            app.ActualThemeVariantChanged += Apply;
            Apply();
        };
    }

    /// <summary>Two-way sync between the editor document and a view-model string.</summary>
    public static void Bind(TextEditor editor, Func<string> get, Action<string> set, Action<Action> onModelChanged)
    {
        var syncing = false;
        editor.Text = get();
        editor.TextChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            set(editor.Text);
            syncing = false;
        };
        onModelChanged(() =>
        {
            if (syncing || editor.Text == get()) return;
            syncing = true;
            // Replace through the document so undo keeps working.
            editor.Document.Replace(0, editor.Document.TextLength, get());
            syncing = false;
        });
    }

    /// <summary>DAX IntelliSense: opens on '[', '\'' or the first letter of a word; Ctrl+Space forces it.</summary>
    public static void EnableDaxCompletion(TextEditor editor, Func<DaxSymbols> symbols)
    {
        CompletionWindow? window = null;

        void Show(bool force)
        {
            if (window is not null || States.GetOrCreateValue(editor).Language != "DAX") return;
            var (from, items) = DaxCompletion.Get(editor.Text, editor.CaretOffset, symbols());
            if (items.Count == 0 || (!force && items.Count == 1 && items[0].Insert.Equals(editor.Text[from..editor.CaretOffset], StringComparison.OrdinalIgnoreCase)))
                return;
            window = new CompletionWindow(editor.TextArea) { StartOffset = from, MinWidth = 280 };
            foreach (var c in items.Take(200)) window.CompletionList.CompletionData.Add(new CompletionItem(c));
            window.Closed += (_, _) => window = null;
            window.Show();
        }

        editor.TextArea.TextEntered += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Text) || window is not null) return;
            var c = e.Text[0];
            var caret = editor.CaretOffset;
            var wordStart = caret >= 2 && (char.IsLetterOrDigit(editor.Text[caret - 2]) || editor.Text[caret - 2] == '_');
            if (c is '[' or '\'' || (char.IsLetter(c) && !wordStart)) Show(force: false);
        };
        editor.TextArea.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                Show(force: true);
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    sealed class CompletionItem(Completion c) : ICompletionData
    {
        public IImage? Image => null;
        public string Text => c.Insert;
        public double Priority => c.Kind == CompletionKind.Variable ? 2 : 1;
        public object Description => c.Detail;

        public object Content => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = c.Kind switch
                    {
                        CompletionKind.Function => "fx", CompletionKind.Measure => "Σ",
                        CompletionKind.Column => "col", CompletionKind.Table => "tbl", _ => "var",
                    },
                    Width = 24, FontSize = 10, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock { Text = c.Label, VerticalAlignment = VerticalAlignment.Center },
            },
        };

        public void Complete(TextArea textArea, ISegment segment, EventArgs e) =>
            textArea.Document.Replace(segment, c.Insert);
    }
}
