using System.IO;
using DocumentFormat.OpenXml.Packaging;
using W=DocumentFormat.OpenXml.Wordprocessing;

namespace ObsidianDocs;

// Minimal, valid DOCX files can omit styles/settings and document relationships.
// SuperDoc's exporter expects these parts. Normalize the in-memory editor input;
// opening a document must never rewrite the user's original file.
internal static class DocxCompatibility
{
    internal static void AddMissingParts(MainDocumentPart main)
    {
        if(main.StyleDefinitionsPart is null)
        {
            var styles=main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles=new W.Styles(
                new W.DocDefaults(new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                    new W.RunFonts { Ascii="Calibri",HighAnsi="Calibri" },new W.FontSize { Val="22" }))),
                new W.Style(new W.StyleName { Val="Normal" }) { Type=W.StyleValues.Paragraph,StyleId="Normal",Default=true });
            styles.Styles.Save();
        }
        if(main.DocumentSettingsPart is null)
        {
            var settings=main.AddNewPart<DocumentSettingsPart>(); settings.Settings=new W.Settings(); settings.Settings.Save();
        }
    }
    internal static byte[] Prepare(byte[] source)
    {
        using(var input=WordprocessingDocument.Open(new MemoryStream(source,false),false))
        {
            var main=input.MainDocumentPart ?? throw new InvalidDataException("В DOCX отсутствует основная часть документа.");
            if(main.StyleDefinitionsPart is not null && main.DocumentSettingsPart is not null) return source;
        }
        using var output=new MemoryStream(); output.Write(source); output.Position=0;
        using(var document=WordprocessingDocument.Open(output,true)) AddMissingParts(document.MainDocumentPart!);
        return output.ToArray();
    }
}
