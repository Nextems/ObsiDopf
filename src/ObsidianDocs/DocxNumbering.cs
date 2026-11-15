using System.Globalization;
using System.Windows.Documents;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ObsidianDocs;
internal sealed partial class DocxEditor
{
    readonly Dictionary<int,int[]> numberCounters=[];
    readonly HashSet<Inline> numberMarkers=[];
    void AddNumbering(Paragraph paragraph,W.Paragraph sourceParagraph,List<W.Style> chain)
    {
        var inherited=chain.Select(s=>s.StyleParagraphProperties?.NumberingProperties).LastOrDefault(p=>p is not null);
        var direct=sourceParagraph.ParagraphProperties?.NumberingProperties;
        int? id=direct?.NumberingId?.Val?.Value ?? inherited?.NumberingId?.Val?.Value;
        if(id is null or 0) return;
        int depth=Math.Clamp(direct?.NumberingLevelReference?.Val?.Value ?? inherited?.NumberingLevelReference?.Val?.Value ?? 0,0,8);
        var root=package.MainDocumentPart?.NumberingDefinitionsPart?.Numbering;
        var instance=root?.Elements<W.NumberingInstance>().FirstOrDefault(n=>n.NumberID?.Value==id);
        var definition=root?.Elements<W.AbstractNum>().FirstOrDefault(n=>n.AbstractNumberId?.Value==instance?.AbstractNumId?.Val?.Value);
        if(instance is null || definition is null) return;
        W.Level? Level(int level)=>instance.Elements<W.LevelOverride>().FirstOrDefault(l=>l.LevelIndex?.Value==level)?.Level ?? definition.Elements<W.Level>().FirstOrDefault(l=>l.LevelIndex?.Value==level);
        var format=Level(depth); if(format is null) return;
        if(!numberCounters.TryGetValue(id.Value,out var counts)) { counts=new int[9]; numberCounters[id.Value]=counts; }
        int start=instance.Elements<W.LevelOverride>().FirstOrDefault(l=>l.LevelIndex?.Value==depth)?.StartOverrideNumberingValue?.Val?.Value ?? format.StartNumberingValue?.Val?.Value ?? 1;
        counts[depth]=counts[depth]==0 ? start : counts[depth]+1;
        for(int i=depth+1;i<counts.Length;i++) counts[i]=0;
        string marker=format.LevelText?.Val?.Value ?? "%1.";
        if(format.NumberingFormat?.Val?.Value==W.NumberFormatValues.Bullet) marker="•";
        else for(int level=0;level<=depth;level++)
        {
            var levelFormat=Level(level);
            marker=marker.Replace("%"+(level+1),FormatNumber(counts[level]==0 ? levelFormat?.StartNumberingValue?.Val?.Value ?? 1 : counts[level],levelFormat?.NumberingFormat?.Val?.Value));
        }
        if(format.NumberingFormat?.Val?.Value==W.NumberFormatValues.None) return;
        ApplyParagraph(paragraph,format.PreviousParagraphProperties);
        foreach(var style in chain) ApplyParagraph(paragraph,style.StyleParagraphProperties);
        ApplyParagraph(paragraph,sourceParagraph.ParagraphProperties);
        var text=new Run(marker+"\t");
        if(paragraph.Inlines.OfType<Run>().FirstOrDefault() is { } first)
        {
            text.FontFamily=first.FontFamily; text.FontSize=first.FontSize; text.FontWeight=first.FontWeight;
        }
        ApplyRun(text,format.NumberingSymbolRunProperties);
        if(format.NumberingFormat?.Val?.Value==W.NumberFormatValues.Bullet) text.FontFamily=new System.Windows.Media.FontFamily("Segoe UI Symbol");
        numberMarkers.Add(text); paragraph.Inlines.InsertBefore(paragraph.Inlines.FirstInline ?? AddEmptyRun(paragraph),text);
    }
    static Run AddEmptyRun(Paragraph paragraph) { var run=new Run(); paragraph.Inlines.Add(run); return run; }
    static string FormatNumber(int number,W.NumberFormatValues? format)
    {
        if(format==W.NumberFormatValues.UpperRoman || format==W.NumberFormatValues.LowerRoman)
        {
            if(number<1 || number>3999) return number.ToString(CultureInfo.InvariantCulture);
            string result="";
            foreach(var (value,label) in new[] { (1000,"M"),(900,"CM"),(500,"D"),(400,"CD"),(100,"C"),(90,"XC"),(50,"L"),(40,"XL"),(10,"X"),(9,"IX"),(5,"V"),(4,"IV"),(1,"I") })
                while(number>=value) { result+=label; number-=value; }
            return format==W.NumberFormatValues.LowerRoman ? result.ToLowerInvariant() : result;
        }
        if(format==W.NumberFormatValues.UpperLetter || format==W.NumberFormatValues.LowerLetter)
        {
            string result="";
            for(int value=Math.Max(1,number);value>0;value=(value-1)/26) result=(char)('A'+(value-1)%26)+result;
            return format==W.NumberFormatValues.LowerLetter ? result.ToLowerInvariant() : result;
        }
        return number.ToString(CultureInfo.InvariantCulture);
    }
}
