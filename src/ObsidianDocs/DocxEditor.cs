using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ObsidianDocs;

// Each tab owns its editor and undo history. Keep package parts and untouched
// blocks verbatim; replacing the entire DOCX with a text export loses drawings,
// relationships, numbering, section properties and headers.
internal sealed partial class DocxEditor : RichTextBox
{
    readonly byte[] source;
    readonly Dictionary<TextElement, OpenXmlElement> originals = [];
    readonly Dictionary<Block, string> initial = [];
    readonly Dictionary<Inline, OpenXmlElement> opaque = [];
    readonly Dictionary<OpenXmlElement,int> bodyIndices = [];
    readonly Dictionary<string,W.Style> styles = [];
    W.DocDefaults? defaults;
    string? defaultStyle;
    WordprocessingDocument package = null!;
    readonly List<TextElement> pageBreakElements=[];
    readonly PagedWordView paged;
    readonly Thickness paperMargins;
    bool paperMarginsReady;
    public event Action? Changed;
    internal bool AdjustingPageGaps { get; set; }
    public FrameworkElement Tools { get; }
    public FrameworkElement View { get; }
    public FrameworkElement MiniMap { get; }
    public int PageCount=>paged.PageCount;
    public event Action? PagesChanged;
    public void SetZoom(double factor)=>paged.SetZoom(factor);
    public void SetFindHighlights(IEnumerable<(TextPointer Start,TextPointer End)> ranges,int active)=>paged.SetFindRanges(ranges,active);
    public IEnumerable<(string Label,Block Block)> Outline()
    {
        int table=0;
        foreach(var b in Document.Blocks)
        {
            if(b is Table) yield return ($"Таблица {++table}",b);
            else if(b is Paragraph p && originals.TryGetValue(p,out var original) && original is W.Paragraph wp)
            {
                var id=wp.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                var style=id is not null && styles.TryGetValue(id,out var s) ? s : null;
                if(wp.ParagraphProperties?.OutlineLevel is not null || style?.StyleParagraphProperties?.OutlineLevel is not null || id?.StartsWith("Heading",StringComparison.OrdinalIgnoreCase)==true || id=="Title")
                {
                    var label=new TextRange(p.ContentStart,p.ContentEnd).Text.Trim();
                    if(label.Length>0) yield return (label.Length>80 ? label[..80]+"…" : label,b);
                }
            }
        }
    }
    static double Number(string? value, double fallback = 0) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    static string N(double value) => (double.IsFinite(value) ? Math.Round(value) : 0).ToString(CultureInfo.InvariantCulture);

    public DocxEditor(byte[] bytes)
    {
        source = bytes;
        Background = Brushes.White;
        Foreground = Brushes.Black; CaretBrush = Brushes.DarkViolet;
        SelectionBrush = new SolidColorBrush(Color.FromRgb(156,127,210));
        BorderThickness = new Thickness(0); Padding = new Thickness(0);
        HorizontalAlignment=HorizontalAlignment.Center;
        Margin=new Thickness(24);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        IsUndoEnabled = true; UndoLimit=50; AcceptsTab = true;
        IsInactiveSelectionHighlightEnabled=true;
        var doc = new FlowDocument { Background = Brushes.White, Foreground = Brushes.Black,
            FontFamily = new FontFamily("Calibri"), FontSize = 11*96d/72,
            PagePadding = new Thickness(64), PageWidth = 794 };
        double paperHeight=1123;
        using (var stream = new MemoryStream(bytes))
        using (package = WordprocessingDocument.Open(stream, false))
        {
            var body=package.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException("В DOCX отсутствует содержимое документа.");
            var section=body.Descendants<W.SectionProperties>().FirstOrDefault();
            if(section?.GetFirstChild<W.PageSize>()?.Width?.Value is { } pageWidth)
                doc.PageWidth=Math.Clamp(pageWidth/15d,200,3000);
            if(section?.GetFirstChild<W.PageSize>()?.Height?.Value is { } pageHeight)
                paperHeight=Math.Clamp(pageHeight/15d,200,5000);
            if(section?.GetFirstChild<W.PageMargin>() is { } margins)
                doc.PagePadding=new Thickness(
                    Math.Max(0,(margins.Left?.Value ?? 960)/15d),
                    Math.Max(0,(margins.Top?.Value ?? 960)/15d),
                    Math.Max(0,(margins.Right?.Value ?? 960)/15d),
                    Math.Max(0,(margins.Bottom?.Value ?? 960)/15d));
            // ColumnWidth is the TEXT width, not the paper width. Adding paper
            // margins to a full-width column made the document overflow.
            doc.ColumnWidth=Math.Max(1,doc.PageWidth-doc.PagePadding.Left-doc.PagePadding.Right);
            var styleRoot=package.MainDocumentPart?.StyleDefinitionsPart?.Styles;
            defaults=styleRoot?.DocDefaults;
            foreach(var style in styleRoot?.Elements<W.Style>() ?? [])
            {
                if(style.StyleId?.Value is { } id) styles[id]=style;
                if(style.Default?.Value==true && style.Type?.Value==W.StyleValues.Paragraph) defaultStyle=style.StyleId?.Value;
            }
            int index=0;
            foreach (var element in body.ChildElements)
            {
                bodyIndices[element]=index++;
                if (ReadBlock(element) is { } block) doc.Blocks.Add(block);
            }
        }
        if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph());
        paperMargins=doc.PagePadding;
        double paperWidth=doc.PageWidth;
        // The editor owns the paper width and margins. Let the bottomless
        // formatter wrap to its inner viewport instead of formatting another
        // full paper width inside those margins.
        doc.PageWidth=double.NaN;
        Padding=paperMargins;
        Document = doc;
        Width=paperWidth;
        paperMarginsReady=true;
        MinHeight=paperHeight;
        // Only the outer view scrolls. An inner horizontal scrollbar shifts the
        // text independently of the paper and clips its left edge at the caret.
        paged=new PagedWordView(this,paperHeight,paperMargins,PageBreaks);
        void HistoryCommand(RoutedCommand command,bool redo)
        {
            CommandBindings.Add(new CommandBinding(command,(_,e)=>
            {
                e.Handled=true;
                paged.KeepViewport(()=> { if(redo) Redo(); else Undo(); });
            },(_,e)=> { e.CanExecute=redo ? CanRedo : CanUndo; e.Handled=true; }));
        }
        HistoryCommand(ApplicationCommands.Undo,false);
        HistoryCommand(ApplicationCommands.Redo,true);
        paged.PagesChanged+=()=>PagesChanged?.Invoke();
        View=new ScrollViewer
        {
            Content=paged,
            Background=new SolidColorBrush(Color.FromRgb(30,30,36)),
            HorizontalContentAlignment=HorizontalAlignment.Center,
            VerticalContentAlignment=VerticalAlignment.Top,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility=ScrollBarVisibility.Hidden
        };
        MiniMap=new WordMiniMap(paged,(ScrollViewer)View,this).View;
        foreach(var block in initial.Keys.ToList()) initial[block]=Fingerprint(block);
        Tools = BuildTools();
        TextChanged += (_,_) => { if(!AdjustingPageGaps) Changed?.Invoke(); };
    }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if(!paperMarginsReady) return;
        // RichTextBox.CreateRenderScope replaces FlowDocument.PagePadding with
        // its caret inset. Preserve Word's real margins in the control padding,
        // subtracting that inset so the same margins are not counted twice.
        var caret=Document.PagePadding;
        static double Inset(double requested,double native)=>Math.Max(0,requested-(double.IsFinite(native) ? native : 0));
        Padding=new Thickness(Inset(paperMargins.Left,caret.Left),Inset(paperMargins.Top,caret.Top),Inset(paperMargins.Right,caret.Right),Inset(paperMargins.Bottom,caret.Bottom));
    }
    IEnumerable<TextPointer> PageBreaks()
    {
        foreach(var element in pageBreakElements)
        {
            DependencyObject? parent=element;
            while(parent is FrameworkContentElement content && parent is not FlowDocument) parent=content.Parent;
            if(parent!=Document) continue;
            yield return element is Paragraph ? element.ContentStart : element.ContentEnd;
        }
    }

    Block? ReadBlock(OpenXmlElement element)
    {
        if(element.LocalName is "bookmarkStart" or "bookmarkEnd" or "proofErr" or "permStart" or "permEnd" or "commentRangeStart" or "commentRangeEnd" or "moveFromRangeStart" or "moveFromRangeEnd" or "moveToRangeStart" or "moveToRangeEnd") return null;
        if(element is W.TableCellProperties) return null;
        Block? block = null;
        if (element is W.Paragraph p)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            var props = p.ParagraphProperties;
            // Resolve paragraph styles for the editor, retaining style IDs on save.
            var chain=StyleChain(props?.ParagraphStyleId?.Val?.Value ?? defaultStyle).ToList();
            ApplyParagraph(paragraph, defaults?.ParagraphPropertiesDefault?.ParagraphPropertiesBaseStyle);
            foreach(var style in chain) ApplyParagraph(paragraph,style.StyleParagraphProperties);
            ApplyParagraph(paragraph, props);
            var before=props?.PageBreakBefore ?? chain.Select(s=>s.StyleParagraphProperties?.PageBreakBefore).LastOrDefault(p=>p is not null);
            if(before is not null && before.Val?.Value!=false) pageBreakElements.Add(paragraph);
            foreach (var run in p.Descendants<W.Run>())
            {
                foreach (var child in run.ChildElements)
                {
                    Inline? inline = child switch
                    {
                        W.Text t => new Run(t.Text),
                        W.TabChar => new Run("\t"),
                        W.Break => new LineBreak(),
                        W.LastRenderedPageBreak => new LineBreak(),
                        W.Drawing => ReadDrawing(child),
                        _ => null
                    };
                    if (inline is null) continue;
                    if (inline is Run r)
                    {
                        ApplyRun(r,defaults?.RunPropertiesDefault?.RunPropertiesBaseStyle);
                        foreach(var style in chain) ApplyRun(r,style.StyleRunProperties);
                        foreach(var style in StyleChain(run.RunProperties?.RunStyle?.Val?.Value)) ApplyRun(r,style.StyleRunProperties);
                        ApplyRun(r, run.RunProperties);
                        originals[r] = run;
                    }
                    paragraph.Inlines.Add(inline);
                    if(child is W.Break { Type.Value:var kind } && kind==W.BreakValues.Page || child is W.LastRenderedPageBreak)
                    {
                        pageBreakElements.Add(inline);
                        opaque[inline]=child;
                    }
                }
            }
            AddNumbering(paragraph,p,chain);
            if(paragraph.Inlines.OfType<Run>().FirstOrDefault(r=>!numberMarkers.Contains(r)) is { } first)
            {
                paragraph.FontFamily=first.FontFamily; paragraph.FontSize=first.FontSize;
            }
            ApplyParagraph(paragraph,defaults?.ParagraphPropertiesDefault?.ParagraphPropertiesBaseStyle);
            foreach(var style in chain) ApplyParagraph(paragraph,style.StyleParagraphProperties);
            ApplyParagraph(paragraph,props);
            block = paragraph;
        }
        else if (element is W.Table t)
        {
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0,8,0,8) };
            var grid = t.GetFirstChild<W.TableGrid>();
            if (grid is not null)
                foreach (var col in grid.Elements<W.GridColumn>()) table.Columns.Add(new TableColumn { Width = new GridLength(Math.Max(12,Number(col.Width?.Value,1440)/15)) });
            var group = new TableRowGroup(); table.RowGroups.Add(group);
            foreach (var row in t.Elements<W.TableRow>())
            {
                var uiRow = new TableRow(); originals[uiRow] = row; group.Rows.Add(uiRow);
                foreach (var cell in row.Elements<W.TableCell>())
                {
                    var uiCell = NewCell(); originals[uiCell] = cell;
                    var cellMargins=cell.TableCellProperties?.ChildElements.FirstOrDefault(e=>e.LocalName=="tcMar") ?? t.TableProperties?.ChildElements.FirstOrDefault(e=>e.LocalName=="tblCellMar");
                    double CellMargin(string edge,double fallback)
                    {
                        var margin=cellMargins?.ChildElements.FirstOrDefault(e=>e.LocalName==edge || edge=="left" && e.LocalName=="start" || edge=="right" && e.LocalName=="end");
                        return margin is null ? fallback : Math.Max(0,Number(margin.GetAttribute("w","http://schemas.openxmlformats.org/wordprocessingml/2006/main").Value)/15);
                    }
                    uiCell.Padding=new Thickness(CellMargin("left",7.2),CellMargin("top",0),CellMargin("right",7.2),CellMargin("bottom",0));
                    uiCell.ColumnSpan = Math.Max(1,cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1);
                    foreach (var item in cell.ChildElements) if (ReadBlock(item) is { } b) uiCell.Blocks.Add(b);
                    if (uiCell.Blocks.Count == 0) uiCell.Blocks.Add(new Paragraph());
                    uiRow.Cells.Add(uiCell);
                }
            }
            block = table;
        }
        else if(element is not W.SectionProperties)
        {
            // Content controls and embedded objects keep their original XML.
            var text=string.Join("\n",element.Descendants<W.Paragraph>().Select(p=>p.InnerText));
            block=new BlockUIContainer(new TextBlock { Text=string.IsNullOrEmpty(text) ? "[Встроенный объект]" : text,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DimGray });
        }
        if (block is not null) { originals[block] = element; initial[block] = ""; }
        return block;
    }

    IEnumerable<W.Style> StyleChain(string? id)
    {
        var chain=new List<W.Style>(); var visited=new HashSet<string>();
        while(id is not null && visited.Add(id) && styles.TryGetValue(id,out var style)) { chain.Add(style); id=style.BasedOn?.Val?.Value; }
        chain.Reverse(); return chain;
    }
    Inline ReadDrawing(OpenXmlElement drawing)
    {
        UIElement view;
        try
        {
            var blip = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().First();
            var part = package.MainDocumentPart!.GetPartById(blip.Embed!.Value!);
            // WPF/WIC may use asynchronous reads even during image setup.
            // Never give it a package's non-seekable DeflateStream: disposal of
            // the DOCX can otherwise race an outstanding native zlib read.
            using var memory=new MemoryStream();
            using(var compressed=part.GetStream(FileMode.Open,FileAccess.Read)) compressed.CopyTo(memory);
            memory.Position=0;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = memory; image.EndInit(); image.Freeze();
            var extent = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().FirstOrDefault();
            view = new Image { Source = image, Width = Math.Min(660,(extent?.Cx?.Value ?? image.PixelWidth*9525L)/9525d), Stretch = Stretch.Uniform };
        }
        catch { view = new TextBlock { Text = "[Объект документа]", Foreground = Brushes.Gray }; }
        var inline = new InlineUIContainer(view); opaque[inline] = drawing; return inline;
    }

    static void ApplyParagraph(Paragraph p, OpenXmlElement? props)
    {
        if (props is null) return;
        if (props.GetFirstChild<W.Justification>() is { } align)
            p.TextAlignment = align.Val?.Value == W.JustificationValues.Center ? TextAlignment.Center : align.Val?.Value == W.JustificationValues.Right ? TextAlignment.Right : align.Val?.Value == W.JustificationValues.Both ? TextAlignment.Justify : TextAlignment.Left;
        if (props.GetFirstChild<W.Indentation>() is { } indent)
        {
            p.Margin = new Thickness(indent.Left is null ? p.Margin.Left : Math.Max(0,Number(indent.Left.Value)/15),p.Margin.Top,indent.Right is null ? p.Margin.Right : Math.Max(0,Number(indent.Right.Value)/15),p.Margin.Bottom);
            if(indent.FirstLine is not null || indent.Hanging is not null) p.TextIndent = (Number(indent.FirstLine?.Value)-Number(indent.Hanging?.Value))/15;
        }
        if (props.GetFirstChild<W.SpacingBetweenLines>() is { } spacing)
        {
            p.Margin = new Thickness(p.Margin.Left,spacing.Before is null ? p.Margin.Top : Number(spacing.Before.Value)/15,p.Margin.Right,spacing.After is null ? p.Margin.Bottom : Number(spacing.After.Value)/15);
            if(spacing.Line is not null)
            {
                double natural=p.FontSize*p.FontFamily.LineSpacing;
                double height=spacing.LineRule?.Value==W.LineSpacingRuleValues.Exact ? Number(spacing.Line.Value)/15 : spacing.LineRule?.Value==W.LineSpacingRuleValues.AtLeast ? Math.Max(natural,Number(spacing.Line.Value)/15) : natural*Number(spacing.Line.Value,240)/240;
                p.LineHeight=Math.Clamp(height,1,10000);
                p.LineStackingStrategy=spacing.LineRule?.Value==W.LineSpacingRuleValues.Exact ? System.Windows.LineStackingStrategy.BlockLineHeight : System.Windows.LineStackingStrategy.MaxHeight;
            }
        }
    }
    static void ApplyRun(Run run, OpenXmlElement? props)
    {
        if (props is null) return;
        if (props.GetFirstChild<W.RunFonts>() is { } f && (f.Ascii?.Value ?? f.HighAnsi?.Value) is { } name) run.FontFamily = new FontFamily(name);
        if (props.GetFirstChild<W.FontSize>() is { } sz) run.FontSize = Math.Clamp(Number(sz.Val?.Value,22)*2/3,1,512);
        if (props.GetFirstChild<W.Bold>() is { } b) run.FontWeight = b.Val?.Value == false ? FontWeights.Normal : FontWeights.Bold;
        if (props.GetFirstChild<W.Italic>() is { } i) run.FontStyle = i.Val?.Value == false ? FontStyles.Normal : FontStyles.Italic;
        if (props.GetFirstChild<W.Underline>() is { } u && u.Val?.Value != W.UnderlineValues.None) run.TextDecorations = TextDecorations.Underline;
        if (props.GetFirstChild<W.Color>()?.Val?.Value is { } color && color.Length == 6)
            try { run.Foreground = (Brush)new BrushConverter().ConvertFromString("#"+color)!; } catch (FormatException) { }
    }

    public byte[] Snapshot()
    {
        using var stream = new MemoryStream(); stream.Write(source); stream.Position = 0;
        using (var doc = WordprocessingDocument.Open(stream,true))
        {
            var main=doc.MainDocumentPart ?? throw new InvalidDataException("В DOCX отсутствует основная часть.");
            var body = main.Document?.Body ?? throw new InvalidDataException("В DOCX отсутствует содержимое документа.");
            var originalChildren = body.ChildElements.ToList();
            var output = new List<OpenXmlElement>();
            var represented = originals.Where(x=>x.Key is Block && bodyIndices.ContainsKey(x.Value)).Select(x=>bodyIndices[x.Value]).ToHashSet();
            // Keep unrepresented items (including section properties) in order.
            int cursor = 0;
            foreach (var b in Document.Blocks)
            {
                if (originals.TryGetValue(b,out var original))
                {
                    int index = bodyIndices.GetValueOrDefault(original,-1);
                    if (index >= cursor)
                    {
                        for (;cursor<index;cursor++) if (!represented.Contains(cursor)) output.Add(originalChildren[cursor].CloneNode(true));
                        cursor = index+1;
                    }
                }
                output.Add(WriteBlock(b));
            }
            for (;cursor<originalChildren.Count;cursor++)
                if (!represented.Contains(cursor)) output.Add(originalChildren[cursor].CloneNode(true));
            body.RemoveAllChildren(); body.Append(output); main.Document!.Save();
        }
        return stream.ToArray();
    }
    OpenXmlElement WriteBlock(Block block)
    {
        originals.TryGetValue(block,out var original);
        if (original is not null && initial.TryGetValue(block,out var fingerprint) && fingerprint == Fingerprint(block)) return original.CloneNode(true);
        if (block is Paragraph p)
        {
            var xml = new W.Paragraph();
            var props = (W.ParagraphProperties?)((original as W.Paragraph)?.ParagraphProperties?.CloneNode(true)) ?? new W.ParagraphProperties();
            props.Justification = new W.Justification { Val = p.TextAlignment switch { TextAlignment.Center => W.JustificationValues.Center, TextAlignment.Right => W.JustificationValues.Right, TextAlignment.Justify => W.JustificationValues.Both, _ => W.JustificationValues.Left } };
            props.Indentation = new W.Indentation { Left = N(p.Margin.Left*15), Right = N(p.Margin.Right*15), FirstLine = p.TextIndent >= 0 ? N(p.TextIndent*15) : null, Hanging = p.TextIndent < 0 ? N(-p.TextIndent*15) : null };
            var spacing = props.SpacingBetweenLines ?? new W.SpacingBetweenLines(); spacing.Before = N(p.Margin.Top*15); spacing.After = N(p.Margin.Bottom*15); props.SpacingBetweenLines = spacing;
            xml.Append(props);
            foreach (var inline in p.Inlines) WriteInline(xml,inline);
            return xml;
        }
        if (block is Table table)
        {
            var xml = new W.Table();
            xml.Append((original as W.Table)?.TableProperties?.CloneNode(true) ?? new W.TableProperties(new W.TableBorders(new W.TopBorder { Val=W.BorderValues.Single,Size=4 },new W.BottomBorder { Val=W.BorderValues.Single,Size=4 },new W.LeftBorder { Val=W.BorderValues.Single,Size=4 },new W.RightBorder { Val=W.BorderValues.Single,Size=4 },new W.InsideHorizontalBorder { Val=W.BorderValues.Single,Size=4 },new W.InsideVerticalBorder { Val=W.BorderValues.Single,Size=4 })));
            var grid = new W.TableGrid(); foreach (var col in table.Columns) grid.Append(new W.GridColumn { Width=N((col.Width.IsAbsolute ? col.Width.Value : 160)*15) }); xml.Append(grid);
            foreach (var group in table.RowGroups) foreach (var row in group.Rows)
            {
                var r = new W.TableRow();
                if (originals.TryGetValue(row,out var oldRow) && oldRow.GetFirstChild<W.TableRowProperties>() is { } rp) r.Append(rp.CloneNode(true));
                foreach (var cell in row.Cells)
                {
                    var c = new W.TableCell();
                    var cp = originals.TryGetValue(cell,out var oldCell) ? (W.TableCellProperties?)oldCell.GetFirstChild<W.TableCellProperties>()?.CloneNode(true) : null;
                    cp ??= new W.TableCellProperties(); cp.GridSpan = cell.ColumnSpan > 1 ? new W.GridSpan { Val=cell.ColumnSpan } : null; c.Append(cp);
                    foreach (var b in cell.Blocks) c.Append(WriteBlock(b));
                    if (c.LastChild is not W.Paragraph) c.Append(new W.Paragraph());
                    r.Append(c);
                }
                xml.Append(r);
            }
            return xml;
        }
        // WPF paste may introduce lists/sections. Retain their paragraph text.
        return new W.Paragraph(new W.Run(new W.Text(new TextRange(block.ContentStart,block.ContentEnd).Text) { Space=SpaceProcessingModeValues.Preserve }));
    }
    void WriteInline(W.Paragraph p, Inline inline)
    {
        if(numberMarkers.Contains(inline)) return;
        if (opaque.TryGetValue(inline,out var element)) { p.Append(new W.Run(element.CloneNode(true))); return; }
        if (inline is Span span) { foreach (var child in span.Inlines) WriteInline(p,child); return; }
        if (inline is LineBreak) { p.Append(new W.Run(new W.Break())); return; }
        if (inline is not Run run) return;
        var props = originals.TryGetValue(run,out var old) ? (W.RunProperties?)old.GetFirstChild<W.RunProperties>()?.CloneNode(true) : null;
        props ??= new W.RunProperties();
        props.RunFonts = new W.RunFonts { Ascii=run.FontFamily.Source, HighAnsi=run.FontFamily.Source, ComplexScript=run.FontFamily.Source };
        props.FontSize = new W.FontSize { Val=N(run.FontSize*1.5) };
        props.Bold = new W.Bold { Val=run.FontWeight >= FontWeights.Bold };
        props.Italic = new W.Italic { Val=run.FontStyle == FontStyles.Italic };
        props.Underline = new W.Underline { Val=run.TextDecorations?.Any(d=>d.Location==TextDecorationLocation.Underline)==true ? W.UnderlineValues.Single : W.UnderlineValues.None };
        if (run.Foreground is SolidColorBrush brush) props.Color = new W.Color { Val=$"{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}" };
        var r = new W.Run(props);
        var pieces = run.Text.Split('\t');
        for (int i=0;i<pieces.Length;i++) { if (i>0) r.Append(new W.TabChar()); r.Append(new W.Text(pieces[i]) { Space=SpaceProcessingModeValues.Preserve }); }
        p.Append(r);
    }

    FrameworkElement BuildTools()
    {
        var panel = new WrapPanel { Margin=new Thickness(0,4,0,2) };
        void Button(string label, Action action) { var b=new Button { Content=label,Focusable=false,Padding=new Thickness(8,6,8,6) }; b.Click+=(_,_)=> { BeginChange(); try { action(); } finally { EndChange(); Focus(); } }; panel.Children.Add(b); }
        void Command(string label, RoutedCommand command)
        {
            if(command!=ApplicationCommands.Undo && command!=ApplicationCommands.Redo)
            { Button(label,()=>command.Execute(null,this)); return; }
            // Undo/redo must run outside a BeginChange transaction. Starting a
            // new edit while undoing interferes with WPF's existing undo units.
            var button=new Button { Content=label,Focusable=false,Padding=new Thickness(8,6,8,6) };
            button.Click+=(_,_)=> { if(command.CanExecute(null,this)) command.Execute(null,this); Focus(); };
            panel.Children.Add(button);
        }
        bool updating=false;
        var fontNames=Fonts.SystemFontFamilies.Select(f=>f.Source).Distinct().OrderBy(x=>x).ToList();
        var fontView=CollectionViewSource.GetDefaultView(fontNames);
        var fonts = new ComboBox { Width=190,Height=32,Margin=new Thickness(3),ItemsSource=fontView,SelectedItem="Calibri",IsEditable=true,IsTextSearchEnabled=false,StaysOpenOnEdit=true,ToolTip="Введите часть названия шрифта и выберите результат · Enter применяет" };
        bool choosingFont=false;
        void ApplyFont(string name)
        {
            choosingFont=true;
            try { fontView.Filter=null; fonts.SelectedItem=name; fonts.Text=name; fonts.IsDropDownOpen=false; Selection.ApplyPropertyValue(TextElement.FontFamilyProperty,new FontFamily(name)); Focus(); }
            finally { choosingFont=false; }
        }
        fonts.SelectionChanged+=(_,_)=> { if (!updating && !choosingFont && fonts.SelectedItem is string name) ApplyFont(name); };
        fonts.AddHandler(TextBoxBase.TextChangedEvent,new TextChangedEventHandler((_,e)=>
        {
            if(updating || choosingFont || e.OriginalSource is not TextBox input || !input.IsKeyboardFocusWithin) return;
            string query=input.Text;
            int caret=input.CaretIndex;
            choosingFont=true;
            try { fontView.Filter=item=>((string)item).Contains(query,StringComparison.CurrentCultureIgnoreCase); fonts.IsDropDownOpen=true; input.Text=query; input.CaretIndex=Math.Min(caret,query.Length); }
            finally { choosingFont=false; }
        }));
        fonts.PreviewKeyDown+=(_,e)=>
        {
            if(e.Key==Key.Enter)
            {
                var match=fontNames.FirstOrDefault(n=>n.Equals(fonts.Text,StringComparison.CurrentCultureIgnoreCase)) ?? fontView.Cast<string>().FirstOrDefault();
                if(match is not null) ApplyFont(match); e.Handled=true;
            }
            else if(e.Key==Key.Escape) { choosingFont=true; try { fonts.IsDropDownOpen=false; fontView.Filter=null; } finally { choosingFont=false; } Focus(); e.Handled=true; }
        };
        panel.Children.Add(fonts);
        var size = new TextBox { Text="11",Width=48,Height=32,Padding=new Thickness(5),Margin=new Thickness(3),ToolTip="Размер шрифта в пунктах · Enter" };
        void ApplySize() { if (double.TryParse(size.Text.Replace(',','.'),CultureInfo.InvariantCulture,out var n) && n>=1 && n<=300) Selection.ApplyPropertyValue(TextElement.FontSizeProperty,n*96/72); Focus(); }
        size.KeyDown+=(_,e)=> { if (e.Key==Key.Enter) { ApplySize(); e.Handled=true; } }; size.LostKeyboardFocus+=(_,_)=> { if (double.TryParse(size.Text.Replace(',','.'),CultureInfo.InvariantCulture,out var n) && n>=1 && n<=300) Selection.ApplyPropertyValue(TextElement.FontSizeProperty,n*96/72); }; panel.Children.Add(size);
        SelectionChanged+=(_,_)=>
        {
            updating=true;
            try
            {
                if(!fonts.IsKeyboardFocusWithin) { fontView.Filter=null; fonts.SelectedItem=(Selection.GetPropertyValue(TextElement.FontFamilyProperty) as FontFamily)?.Source; }
                if(!size.IsKeyboardFocusWithin) size.Text=Selection.GetPropertyValue(TextElement.FontSizeProperty) is double n ? (n*72/96).ToString("0.#",CultureInfo.CurrentCulture) : "";
            }
            finally { updating=false; }
        };
        Command("Ж",EditingCommands.ToggleBold); Command("К",EditingCommands.ToggleItalic); Command("Ч",EditingCommands.ToggleUnderline);
        Command("Слева",EditingCommands.AlignLeft); Command("Центр",EditingCommands.AlignCenter); Command("Справа",EditingCommands.AlignRight); Command("По ширине",EditingCommands.AlignJustify);
        Command("↶",ApplicationCommands.Undo); Command("↷",ApplicationCommands.Redo);
        void Indent(string label, Action<Paragraph,double> apply, Func<Paragraph,double> read)
        {
            panel.Children.Add(new TextBlock { Text=label,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(7,0,2,0),Foreground=new SolidColorBrush(Color.FromRgb(216,213,226)) });
            var box=new TextBox { Text="0",Width=43,Height=32,Padding=new Thickness(5),Margin=new Thickness(3),ToolTip="Сантиметры · Enter" }; panel.Children.Add(box);
            box.KeyDown+=(_,e)=> { if(e.Key!=Key.Enter) return; e.Handled=true; if(!double.TryParse(box.Text.Replace(',','.'),CultureInfo.InvariantCulture,out var cm) || cm<0 || cm>15) return; BeginChange(); try { foreach(var p in SelectedParagraphs()) apply(p,cm*96/2.54); } finally { EndChange(); Focus(); } };
            SelectionChanged+=(_,_)=> { if(!box.IsKeyboardFocusWithin && CaretPosition.Paragraph is { } p) box.Text=(read(p)*2.54/96).ToString("0.##",CultureInfo.CurrentCulture); };
        }
        Indent("Отступ слева",(p,n)=>p.Margin=new Thickness(n,p.Margin.Top,p.Margin.Right,p.Margin.Bottom),p=>p.Margin.Left);
        Indent("справа",(p,n)=>p.Margin=new Thickness(p.Margin.Left,p.Margin.Top,n,p.Margin.Bottom),p=>p.Margin.Right);
        Indent("первая строка",(p,n)=>p.TextIndent=n,p=>p.TextIndent);
        var rows=new TextBox { Text="3",Width=30,Padding=new Thickness(3),Margin=new Thickness(3),ToolTip="Строк в новой таблице" }; panel.Children.Add(rows);
        var cols=new TextBox { Text="3",Width=30,Padding=new Thickness(3),Margin=new Thickness(3),ToolTip="Столбцов в новой таблице" }; panel.Children.Add(cols);
        Button("＋ Таблица",()=> { if(int.TryParse(rows.Text,out int r) && int.TryParse(cols.Text,out int c) && r is >0 and <=100 && c is >0 and <=20) InsertTable(r,c); });
        Button("＋ Строка",()=>ChangeTable("row")); Button("＋ Столбец",()=>ChangeTable("column"));
        Button("− Строка",()=>ChangeTable("deleteRow")); Button("− Столбец",()=>ChangeTable("deleteColumn")); Button("Удалить таблицу",()=>ChangeTable("delete"));
        return panel;
    }
    IEnumerable<Paragraph> SelectedParagraphs()
    {
        var found=new HashSet<Paragraph>();
        for(var p=Selection.Start;p is not null && p.CompareTo(Selection.End)<=0;p=p.GetNextContextPosition(LogicalDirection.Forward))
            if(p.Paragraph is { } paragraph && found.Add(paragraph)) yield return paragraph;
    }
    static TableCell NewCell() => new() { BorderBrush=Brushes.Gray,BorderThickness=new Thickness(.5),Padding=new Thickness(6) };
    static string Fingerprint(Block block)
    {
        var text=new StringBuilder();
        void Value(object? value) { var str=Convert.ToString(value,CultureInfo.InvariantCulture) ?? ""; text.Append(str.Length).Append(':').Append(str); }
        void Inline(Inline inline)
        {
            Value(inline.GetType().Name); Value(inline.FontFamily.Source); Value(inline.FontSize); Value(inline.FontWeight); Value(inline.FontStyle); Value(inline.Foreground);
            if(inline.TextDecorations is { } decorations) foreach(var decoration in decorations) Value(decoration.Location);
            if(inline is Run run) Value(run.Text);
            else if(inline is Span span) foreach(var child in span.Inlines) Inline(child);
            else if(inline is InlineUIContainer ui) Value(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ui));
        }
        void Block(Block b)
        {
            Value(b.GetType().Name); Value(b.Margin); Value(b.TextAlignment);
            if(b is Paragraph p) { Value(p.TextIndent); foreach(var inline in p.Inlines) Inline(inline); }
            else if(b is Table t)
            {
                foreach(var col in t.Columns) Value(col.Width);
                foreach(var group in t.RowGroups) { Value("group"); foreach(var row in group.Rows) { Value("row"); foreach(var cell in row.Cells) { Value("cell"); Value(cell.ColumnSpan); Value(cell.RowSpan); foreach(var child in cell.Blocks) Block(child); } } }
            }
            else Value(new TextRange(b.ContentStart,b.ContentEnd).Text);
        }
        Block(block); return text.ToString();
    }
    void InsertTable(int rows,int columns)
    {
        var table=new Table { CellSpacing=0 }; var group=new TableRowGroup(); table.RowGroups.Add(group);
        for(int c=0;c<columns;c++) table.Columns.Add(new TableColumn { Width=new GridLength(600d/columns) });
        for(int r=0;r<rows;r++) { var row=new TableRow(); group.Rows.Add(row); for(int c=0;c<columns;c++) { var cell=NewCell(); cell.Blocks.Add(new Paragraph()); row.Cells.Add(cell); } }
        var paragraph=CaretPosition.Paragraph;
        if(paragraph?.Parent is FlowDocument) Document.Blocks.InsertAfter(paragraph,table);
        else Document.Blocks.Add(table);
        CaretPosition=group.Rows[0].Cells[0].ContentStart;
    }
    void ChangeTable(string operation)
    {
        DependencyObject? parent=CaretPosition.Paragraph;
        while(parent is not null && parent is not TableCell) parent=(parent as FrameworkContentElement)?.Parent;
        if(parent is not TableCell cell || cell.Parent is not TableRow row || row.Parent is not TableRowGroup group || group.Parent is not Table table) return;
        if(operation=="delete") { RemoveTable(table); return; }
        // Structural changes to merged tables require a layout engine. Text and
        // formatting inside their existing cells remain editable.
        if(group.Rows.Any(r=>r.Cells.Any(c=>c.ColumnSpan!=1 || c.RowSpan!=1)) || (originals.TryGetValue(table,out var sourceTable) && sourceTable.Descendants<W.VerticalMerge>().Any())) return;
        int index=row.Cells.IndexOf(cell);
        if(operation=="row") { var added=new TableRow(); for(int i=0;i<row.Cells.Count;i++) { var c=NewCell(); c.Blocks.Add(new Paragraph()); added.Cells.Add(c); } group.Rows.Insert(group.Rows.IndexOf(row)+1,added); }
        if(operation=="deleteRow") { if(group.Rows.Count==1) RemoveTable(table); else group.Rows.Remove(row); }
        if(operation=="column") { foreach(var r in group.Rows) { var c=NewCell(); c.Blocks.Add(new Paragraph()); r.Cells.Insert(Math.Min(index+1,r.Cells.Count),c); } table.Columns.Insert(Math.Min(index+1,table.Columns.Count),new TableColumn { Width=new GridLength(100) }); }
        if(operation=="deleteColumn") { if(row.Cells.Count==1) { RemoveTable(table); return; } foreach(var r in group.Rows) if(index<r.Cells.Count) r.Cells.RemoveAt(index); if(index<table.Columns.Count) table.Columns.RemoveAt(index); }
    }
    static void RemoveTable(Table table)
    {
        if(table.Parent is FlowDocument doc) { doc.Blocks.Remove(table); if(doc.Blocks.Count==0) doc.Blocks.Add(new Paragraph()); }
        else if(table.Parent is TableCell cell) { cell.Blocks.Remove(table); if(cell.Blocks.Count==0) cell.Blocks.Add(new Paragraph()); }
    }
}
