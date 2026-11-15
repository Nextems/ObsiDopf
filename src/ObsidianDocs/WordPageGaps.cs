using System.Windows;
using System.Windows.Documents;

namespace ObsidianDocs;
internal sealed class WordPageGaps
{
    public const double Size=8;
    sealed record State(Style Normal,Style Spaced);
    internal sealed record Target(List<FrameworkContentElement> Elements,List<TextPointer> Anchors);
    readonly Dictionary<FrameworkContentElement,State> states=[];
    readonly Dictionary<Style,State> presentationStyles=[];
    readonly HashSet<FrameworkContentElement> spacedElements=[];
    readonly DocxEditor editor;
    public WordPageGaps(DocxEditor editor)=>this.editor=editor;
    static DependencyProperty PaddingProperty(FrameworkContentElement element)=>element is TableCell ? TableCell.PaddingProperty : Block.PaddingProperty;
    void Prepare(FrameworkContentElement element)
    {
        if(states.ContainsKey(element)) return;
        // Undo can recreate a paragraph carrying one of our existing styles.
        // Reuse its original padding instead of wrapping the gap style again
        // and accumulating another 8 pixels on every undo/redo cycle.
        if(element.Style is { } existing && presentationStyles.TryGetValue(existing,out var restored))
        {
            states[element]=restored;
            if(existing==restored.Spaced) spacedElements.Add(element);
            return;
        }
        var property=PaddingProperty(element);
        // Clearing a newly inserted cell's explicit formatting is a document
        // edit. Do not inject such edits into the user's undo/redo history just
        // to draw a page gap; use the next eligible boundary instead.
        if(editor.IsUndoEnabled && element.ReadLocalValue(property)!=DependencyProperty.UnsetValue) return;
        var value=(Thickness)element.GetValue(property);
        double Top=double.IsFinite(value.Top) ? value.Top : 0;
        var normal=new Style(element.GetType(),element.Style);
        normal.Setters.Add(new Setter(property,value));
        var spaced=new Style(element.GetType(),normal);
        spaced.Setters.Add(new Setter(property,new Thickness(value.Left,Top+Size,value.Right,value.Bottom)));
        if(element.ReadLocalValue(property)!=DependencyProperty.UnsetValue) element.ClearValue(property);
        element.Style=normal;
        var state=new State(normal,spaced);
        states[element]=state;
        presentationStyles[normal]=presentationStyles[spaced]=state;
    }
    public List<Target> GetTargets()
    {
        var targets=new List<Target>();
        bool first=true;
        foreach(var block in editor.Document.Blocks)
        {
            Prepare(block);
            if(!first && states.ContainsKey(block)) targets.Add(new Target([block],[block.ContentStart]));
            first=false;
            if(block is not Table table) continue;
            bool firstRow=true;
            foreach(var group in table.RowGroups) foreach(var row in group.Rows)
            {
                foreach(var cell in row.Cells) Prepare(cell);
                if(!firstRow && row.Cells.Count>0 && row.Cells.All(c=>states.ContainsKey(c))) targets.Add(new Target(row.Cells.Cast<FrameworkContentElement>().ToList(),row.Cells.Select(c=>c.ContentStart).ToList()));
                firstRow=false;
            }
        }
        return targets;
    }
    public bool Apply(IEnumerable<Target> targets)
    {
        bool changed=false;
        var desired=targets.SelectMany(t=>t.Elements).ToHashSet();
        foreach(var element in spacedElements.Except(desired).ToList())
        {
            element.Style=states[element].Normal;
            spacedElements.Remove(element);
            changed=true;
        }
        foreach(var element in desired)
        {
            if(!spacedElements.Add(element)) continue;
            element.Style=states[element].Spaced;
            changed=true;
        }
        return changed;
    }
    public bool IsSpaced(Target target)=>target.Elements.Any(spacedElements.Contains);
    public static double Position(Target target)
    {
        double y=double.PositiveInfinity;
        foreach(var anchor in target.Anchors)
        {
            try { var rect=anchor.GetCharacterRect(LogicalDirection.Forward); if(!rect.IsEmpty) y=Math.Min(y,rect.Top); }
            catch(InvalidOperationException) { }
        }
        return y;
    }
}
