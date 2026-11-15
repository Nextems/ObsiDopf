using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Drawing;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ObsidianDocs;

public sealed class DocumentSession : IDisposable
{
    public required string Path { get; set; }
    public PdfDocument? Pdf { get; set; }
    public BitmapSource? Photo { get; set; }
    public byte[]? RenderBytes { get; set; }
    public byte[]? DocxBytes { get; set; }
    internal WordEditor? Editor { get; set; }
    public int Page { get; set; }
    public bool Dirty { get; set; }
    public bool IsUntitled { get; set; }
    readonly List<(byte[] Bytes,int Page)> pdfUndo=[];
    public bool CanUndoPdf=>pdfUndo.Count>0;
    void RememberPdfSnapshot()
    {
        if(RenderBytes is null) return;
        pdfUndo.Add((RenderBytes,Page));
        while(pdfUndo.Count>1 && (pdfUndo.Count>10 || pdfUndo.Sum(s=>(long)s.Bytes.Length)>128L*1024*1024)) pdfUndo.RemoveAt(0);
    }
    public void ApplyPdfEdit(Action<PdfDocument> edit,int? targetPage=null)
    {
        if(!CanEditPdf || RenderBytes is null) throw new InvalidOperationException("Правки доступны только в PDF.");
        using var copy=PdfReader.Open(new MemoryStream(RenderBytes),PdfDocumentOpenMode.Modify);
        edit(copy);
        using var output=new MemoryStream(); copy.Save(output,false);
        var bytes=output.ToArray();
        var editable=PdfReader.Open(new MemoryStream(bytes),PdfDocumentOpenMode.Modify);
        RememberPdfSnapshot(); var previous=Pdf; Pdf=editable; RenderBytes=bytes;
        Page=Math.Clamp(targetPage ?? Page,0,editable.PageCount-1); Dirty=true; previous.Dispose();
    }
    public void UndoPdfEdit()
    {
        if(!CanUndoPdf || Pdf is null || IsDocx) return;
        var snapshot=pdfUndo[^1];
        var editable=PdfReader.Open(new MemoryStream(snapshot.Bytes),PdfDocumentOpenMode.Modify);
        pdfUndo.RemoveAt(pdfUndo.Count-1); var previous=Pdf; Pdf=editable; RenderBytes=snapshot.Bytes;
        Page=Math.Clamp(snapshot.Page,0,editable.PageCount-1); Dirty=true; previous.Dispose();
    }
    public bool IsDocx => System.IO.Path.GetExtension(Path).Equals(".docx", StringComparison.OrdinalIgnoreCase);
    public bool IsDoc => System.IO.Path.GetExtension(Path).Equals(".doc", StringComparison.OrdinalIgnoreCase);
    public bool IsWordDocument => IsDocx || IsDoc;
    [MemberNotNullWhen(true,nameof(Pdf))]
    public bool CanEditPdf => Pdf is not null && System.IO.Path.GetExtension(Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public int Count => Pdf?.PageCount ?? 1;
    public string Name => System.IO.Path.GetFileName(Path);
    public void Snapshot()
    {
        if (Pdf is null) return;
        // PDFsharp seals a document after Save. Keep a fresh editable instance
        // and a separate byte snapshot for rendering and repeated disk writes.
        using var output = new MemoryStream();
        Pdf.Save(output, false);
        var bytes = output.ToArray();
        var editable = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Modify);
        RememberPdfSnapshot();
        var previous = Pdf;
        Pdf = editable;
        RenderBytes = bytes;
        previous.Dispose();
    }
    public void SaveTo(string path)
    {
        if(IsWordDocument) throw new InvalidOperationException("Для сохранения документа Word используйте редактор DOCX или экспорт в PDF.");
        if (RenderBytes is null) throw new InvalidOperationException("Нет PDF для сохранения.");
        var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, Guid.NewGuid() + ".tmp");
        try { File.WriteAllBytes(temp, RenderBytes); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void SaveChanges()
    {
        if (Pdf is null || !System.IO.Path.GetExtension(Path).Equals(".pdf",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Для этого документа используйте «Сохранить как…» и выберите PDF.");
        SaveTo(Path);
        Dirty=false;
    }
    public void DuplicateWithAuthor(string destination, string author)
    {
        author = author.Trim();
        if (string.IsNullOrWhiteSpace(author) || author.Length > 100 || author.Any(char.IsControl)) throw new ArgumentException("Укажите корректное имя автора.");
        if (System.IO.Path.GetFullPath(destination).Equals(System.IO.Path.GetFullPath(Path),StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Для копии выберите другое имя файла. Оригинал должен сохраниться.");
        if (RenderBytes is null || !CanEditPdf) throw new InvalidOperationException("Копирование комментариев доступно для PDF.");
        using var copy = PdfReader.Open(new MemoryStream(RenderBytes),PdfDocumentOpenMode.Modify);
        foreach (var page in copy.Pages)
            foreach (PdfSharp.Pdf.Annotations.PdfAnnotation note in page.Annotations)
                if (!string.IsNullOrWhiteSpace(note.Contents) || !string.IsNullOrWhiteSpace(note.Title)) note.Title=author;
        SaveAtomic(copy,destination);
    }
    readonly object previewLock=new();
    byte[]? previewSource,previewBytes;
    byte[] WithoutNativeNoteIcons()
    {
        lock (previewLock)
        {
            var bytes=RenderBytes!;
            if (ReferenceEquals(bytes,previewSource)) return previewBytes!;
            using var preview=PdfReader.Open(new MemoryStream(bytes),PdfDocumentOpenMode.Modify);
            bool changed=false;
            foreach (var page in preview.Pages)
            {
                if (!page.Elements.ContainsKey("/Annots")) continue;
                for (int i=page.Annotations.Count-1;i>=0;i--)
                    if (page.Annotations[i].Elements.GetName("/Subtype")=="/Text") { page.Annotations.Remove(page.Annotations[i]); changed=true; }
            }
            if (changed) { using var output=new MemoryStream(); preview.Save(output,false); previewBytes=output.ToArray(); }
            else previewBytes=bytes;
            previewSource=bytes;
            return previewBytes;
        }
    }
    public BitmapSource Render(int page, int dpi = 144, bool includeNoteIcons = true)
    {
        if (Photo is not null) return Photo;
        // The interactive viewer draws its own comment bubbles. Only its
        // cached raster omits native sticky-note icons; files retain all notes.
        using var input = new MemoryStream(includeNoteIcons ? RenderBytes! : WithoutNativeNoteIcons());
        using var bitmap = PDFtoImage.Conversion.ToImage(input, page: page, options: new PDFtoImage.RenderOptions(Dpi: dpi,WithAnnotations:true));
        using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var memory = new MemoryStream(encoded.ToArray());
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = memory; image.EndInit(); image.Freeze(); return image;
    }
    public static DocumentSession Open(string path,CancellationToken cancellation=default,IProgress<string>? progress=null)
    {
        cancellation.ThrowIfCancellationRequested();
        var session = new DocumentSession { Path = path };
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".pdf")
        {
            var data = File.ReadAllBytes(path);
            session.Pdf = PdfReader.Open(new MemoryStream(data), PdfDocumentOpenMode.Modify);
            session.RenderBytes = data;
            if (session.Count == 0) throw new InvalidDataException("В PDF нет страниц.");
        }
        else if (extension == ".docx")
        {
            session.DocxBytes = File.ReadAllBytes(path);
        }
        else if (extension == ".doc")
        {
            session.DocxBytes=DocConversionCache.ConvertToDocx(path,cancellation,progress);
        }
        else
        {
            using var input = File.OpenRead(path);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = input; image.EndInit(); image.Freeze(); session.Photo = image;
        }
        return session;
    }
    public static PdfDocument TextPdf(IEnumerable<string> lines)
    {
        var pdf = new PdfDocument();
        XGraphics? graphics = null; double y = 0;
        var font = new XFont("Arial", 11);
        void NewPage() { graphics?.Dispose(); var page = pdf.AddPage(); graphics = XGraphics.FromPdfPage(page); y = 48; }
        NewPage();
        foreach (var line in lines)
        {
            var current = "";
            foreach (var word in line.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (graphics!.MeasureString(candidate, font).Width > 490 && current.Length > 0)
                { Draw(current); current = word; }
                else current = candidate;
            }
            Draw(current);
        }
        graphics?.Dispose(); return pdf;
        void Draw(string text) { if (y > 790) NewPage(); graphics!.DrawString(text, font, XBrushes.Black, new XPoint(48, y)); y += 17; }
    }
    public static void SaveAtomic(PdfDocument pdf, string path)
    {
        var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, Guid.NewGuid() + ".tmp");
        try { pdf.Save(temp); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void WriteDocx(List<string> pages, string destination)
    {
        var temp = destination + "." + Guid.NewGuid() + ".tmp";
        try
        {
            using (var document = WordprocessingDocument.Create(temp, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart(); main.Document = new W.Document(new W.Body());
                DocxCompatibility.AddMissingParts(main);
                for (int i = 0; i < pages.Count; i++)
                {
                    if (i > 0) main.Document.Body!.Append(new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })));
                    foreach (var line in pages[i].Split('\n')) main.Document.Body!.Append(new W.Paragraph(new W.Run(new W.Text(line) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve })));
                }
                main.Document.Save();
            }
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Dispose() { Editor?.Dispose(); Pdf?.Dispose(); }
}



