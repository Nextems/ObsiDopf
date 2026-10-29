using System.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
namespace ObsidianDocs;
public static class SearchablePdf
{
    // Preserve the rendered page as the visible surface; add transparent Unicode glyphs.
    public static void AddTextLayer(PdfPage page, OcrResult result)
    {
        using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        var font = new XFont("Arial", 12);
        var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));
        foreach (var region in result.Regions)
        {
            if (string.IsNullOrWhiteSpace(region.Text)) continue;
            var state = graphics.Save();
            var measured = graphics.MeasureString(region.Text, font);
            var width = region.W * page.Width.Point; var height = region.H * page.Height.Point;
            graphics.TranslateTransform(region.X * page.Width.Point, region.Y * page.Height.Point);
            graphics.ScaleTransform(width/Math.Max(1, measured.Width), height/Math.Max(1, measured.Height));
            graphics.DrawString(region.Text, font, invisible, new XPoint(0, font.Size));
            graphics.Restore(state);
        }
    }
    public static PdfPage AddPage(PdfDocument pdf, System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        using var data = new MemoryStream();
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); encoder.Save(data); data.Position=0;
        using var image = XImage.FromStream(data);
        var page = pdf.AddPage(); page.Width = XUnit.FromPoint(bitmap.PixelWidth * .5); page.Height = XUnit.FromPoint(bitmap.PixelHeight * .5);
        using (var graphics = XGraphics.FromPdfPage(page)) graphics.DrawImage(image,0,0,page.Width.Point,page.Height.Point);
        return page;
    }
}
