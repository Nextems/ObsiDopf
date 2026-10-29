using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ObsidianDocs;
internal static class LayoutRegression
{
    internal static void Run(string folder)
    {
        var path = Path.Combine(folder,"layout.docx");
        using (var doc = WordprocessingDocument.Create(path,DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart(); var body = new W.Body(); main.Document = new W.Document(body);
            var header = main.AddNewPart<HeaderPart>(); header.Header = new W.Header(new W.Paragraph(new W.Run(new W.Text("LAYOUT HEADER"))));
            var footer = main.AddNewPart<FooterPart>(); footer.Footer = new W.Footer(new W.Paragraph(new W.Run(new W.Text("LAYOUT FOOTER"))));
            body.Append(new W.Paragraph(new W.Run(new W.RunProperties(new W.Bold(),new W.Color { Val="7652A0" },new W.FontSize { Val="40" }),new W.Text("DOCX layout regression"))));
            var table = new W.Table(new W.TableProperties(new W.TableBorders(new W.TopBorder { Val=W.BorderValues.Single,Size=8 },new W.BottomBorder { Val=W.BorderValues.Single,Size=8 },new W.LeftBorder { Val=W.BorderValues.Single,Size=8 },new W.RightBorder { Val=W.BorderValues.Single,Size=8 },new W.InsideHorizontalBorder { Val=W.BorderValues.Single,Size=8 },new W.InsideVerticalBorder { Val=W.BorderValues.Single,Size=8 })));
            for (int row=0;row<3;row++) table.Append(new W.TableRow(new W.TableCell(new W.Paragraph(new W.Run(new W.Text($"Cell {row} A")))),new W.TableCell(new W.Paragraph(new W.Run(new W.Text($"Cell {row} B"))))));
            body.Append(table);
            var visual = new DrawingVisual(); using (var dc=visual.RenderOpen()) { dc.DrawRectangle(Brushes.MediumPurple,null,new System.Windows.Rect(0,0,180,90)); dc.DrawEllipse(Brushes.Gold,null,new System.Windows.Point(90,45),25,25); }
            var bitmap = new RenderTargetBitmap(180,90,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var imageData = new MemoryStream(); encoder.Save(imageData); imageData.Position=0;
            var image=main.AddImagePart(ImagePartType.Png); image.FeedData(imageData); var id=main.GetIdOfPart(image);
            body.Append(new W.Paragraph(new W.Run(new W.Drawing($"""
            <w:drawing xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><wp:inline><wp:extent cx="2286000" cy="1143000"/><wp:docPr id="1" name="Fixture image"/><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic><pic:nvPicPr><pic:cNvPr id="0" name="fixture.png"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed="{id}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="2286000" cy="1143000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>
            """))));
            body.Append(new W.Paragraph(new W.Run(new W.Break { Type=W.BreakValues.Page })),new W.Paragraph(new W.Run(new W.Text("SECOND PAGE MARKER"))));
            body.Append(new W.SectionProperties(new W.HeaderReference { Type=W.HeaderFooterValues.Default,Id=main.GetIdOfPart(header) },new W.FooterReference { Type=W.HeaderFooterValues.Default,Id=main.GetIdOfPart(footer) }));
            main.Document.Save();
        }
        var original = File.ReadAllBytes(path);
        using var session=DocumentSession.Open(path); session.SaveTo(Path.Combine(folder,"layout.pdf"));
        if (!original.SequenceEqual(File.ReadAllBytes(path))) throw new Exception("DOCX source was modified during preview");
        using var pdf=UglyToad.PdfPig.PdfDocument.Open(session.RenderBytes!);
        if (pdf.NumberOfPages != 2 || !pdf.GetPage(1).Text.Contains("LAYOUT HEADER") || !pdf.GetPage(1).Text.Contains("Cell 2 B") || !pdf.GetPage(2).Text.Contains("SECOND PAGE MARKER") || !pdf.GetPage(2).Text.Contains("LAYOUT FOOTER") || !pdf.GetPage(1).GetImages().Any()) throw new Exception("DOCX layout regression failed");
        var output = new PngBitmapEncoder(); output.Frames.Add(BitmapFrame.Create(session.Render(0))); using var file=File.Create(Path.Combine(folder,"docx-layout.png")); output.Save(file);
    }
}
