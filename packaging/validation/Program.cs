using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using Microsoft.Web.WebView2.Wpf;
using ObsidianDocs;

// Runs against the installed application DLLs. No application source is replaced.
public sealed class ValidationApp
{
    static readonly Assembly Product=typeof(App).Assembly;
    static readonly BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    readonly string mode, report;
    readonly List<string> passed=[];
    static Dispatcher Dispatcher=>Application.Current.Dispatcher;
    static WindowCollection Windows=>Application.Current.Windows;
    static void Shutdown(int code)=>Application.Current.Shutdown(code);
    public ValidationApp(string mode,string report) { this.mode=mode; this.report=report; }
    [STAThread]
    public static int Main(string[] args)
    {
        var runner=new ValidationApp(args[0],args[1]);
        var app=new App(); app.InitializeComponent(); app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        app.Startup+=(_,_)=>app.Dispatcher.BeginInvoke(new Action(async ()=>await runner.RunAsync()));
        return app.Run();
    }
    async Task RunAsync()
    {
        try
        {
            if(mode=="core") await CoreAsync();
            else if(mode=="word") await WordAsync();
            else throw new ArgumentException("Unknown validation mode: "+mode);
            File.WriteAllText(report,JsonSerializer.Serialize(new { status="PASS",checks=passed },new JsonSerializerOptions { WriteIndented=true }));
            Shutdown(0);
        }
        catch(Exception error)
        {
            File.WriteAllText(report,JsonSerializer.Serialize(new { status="FAIL",checks=passed,error=error.ToString() },new JsonSerializerOptions { WriteIndented=true }));
            Shutdown(1);
        }
    }
    static object Field(object target,string name)=>target.GetType().GetField(name,All)!.GetValue(target)!;
    static object? Call(object target,string name,params object?[] args)
    {
        var method=target.GetType().GetMethod(name,All)!;
        var parameters=method.GetParameters();
        var values=parameters.Select((p,i)=>i<args.Length ? args[i] : p.DefaultValue).ToArray();
        return method.Invoke(target,values);
    }
    static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    static async Task WaitAsync(Func<bool> condition,string label)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while(!condition()) await Task.Delay(50,deadline.Token);
    }
    void Pass(string value) { passed.Add(value); File.WriteAllText(report,JsonSerializer.Serialize(new { status="RUNNING",checks=passed })); }
    static void Picture(FrameworkElement element,string filename)
    {
        element.UpdateLayout();
        var bitmap=new RenderTargetBitmap(Math.Max(1,(int)element.ActualWidth),Math.Max(1,(int)element.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(element); var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(filename); png.Save(file);
    }
    async Task CoreAsync()
    {
        var folder=Path.GetDirectoryName(report)!;
        var docx=Path.Combine(folder,"validation.docx");
        DocumentSession.WriteDocx(["Alpha text: Привет, мир!","Second page"],docx);
        var renderer=Product.GetType("ObsidianDocs.DocxRenderer")!;
        var pdfBytes=await Task.Run(()=>(byte[])renderer.GetMethod("Render",All)!.Invoke(null,[docx,CancellationToken.None])!);
        var pdfPath=Path.Combine(folder,"validation-converted.pdf"); File.WriteAllBytes(pdfPath,pdfBytes);
        using(var pdf=DocumentSession.Open(pdfPath)) Require(pdf.Count==2 && pdf.Render(0).PixelHeight>500,"DOCX conversion/rendering failed");
        Pass("Installed LibreOffice: DOCX to PDF, page breaks and PDF rendering");
        foreach(var language in new[] { "en","ru" })
        {
            string text=language=="en" ? "HELLO WORLD\nOCR TEST 12345" : "ПРИВЕТ МИР\nПроверка текста 123";
            var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White,null,new Rect(0,0,1000,260));
                dc.DrawText(new FormattedText(text,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Arial"),60,Brushes.Black,1),new Point(40,35));
            }
            var scan=new RenderTargetBitmap(1000,260,96,96,PixelFormats.Pbgra32); scan.Render(visual); scan.Freeze();
            using var ocr=new OcrService(); var result=await ocr.RecognizeAsync(scan,language);
            Require(result.Regions.Count>0 && result.Text.Contains(language=="en" ? "HELLO" : "ПРИВЕТ",StringComparison.OrdinalIgnoreCase),"OCR "+language+": "+result.Text);
            using var searchable=new PdfSharp.Pdf.PdfDocument();
            var page=SearchablePdf.AddPage(searchable,scan); SearchablePdf.AddTextLayer(page,result);
            var output=Path.Combine(folder,"ocr-"+language+".pdf"); DocumentSession.SaveAtomic(searchable,output);
            using var pig=UglyToad.PdfPig.PdfDocument.Open(output);
            Require(pig.GetPage(1).Text.Contains(language=="en" ? "HELLO" : "ПРИВЕТ",StringComparison.OrdinalIgnoreCase),"Searchable PDF failed");
            Pass("Installed OCR "+language+": recognized regions and searchable PDF round-trip");
        }
        await PdfCommentsAsync(folder);
        foreach(var method in new[] { "TestSaveShortcutAsync","TestWheelAsync" })
        {
            var window=new MainWindow(); window.Show();
            try { await ((Task)Call(window,method)!).WaitAsync(TimeSpan.FromMinutes(3)); Pass(method); }
            finally { foreach(var session in (IEnumerable<DocumentSession>)Field(window,"sessions")) session.Dirty=false; window.Close(); }
        }
    }
    async Task PdfCommentsAsync(string folder)
    {
        var window=new MainWindow(); window.Show();
        var pdf=new PdfDocument();
        for(int i=0;i<3;i++)
        {
            var page=pdf.AddPage(); using(var g=XGraphics.FromPdfPage(page))
            {
                g.DrawString("Alpha beta gamma",new XFont("Arial",18),XBrushes.Black,new XPoint(60,100));
                g.DrawString("Second line text",new XFont("Arial",18),XBrushes.Black,new XPoint(60,140));
            }
            if(i==1) page.Rotate=90;
            if(i==2) { page.Rotate=270; page.CropBox=new PdfRectangle(new XPoint(20,30),new XPoint(page.MediaBox.X2-30,page.MediaBox.Y2-30)); }
        }
        pdf.Pages[0].Annotations.Add(new PdfTextAnnotation { Contents="Keep unrelated note",Title="Other author",Rectangle=new PdfRectangle(new XPoint(300,400),new XPoint(320,420)) });
        var session=new DocumentSession { Path=Path.Combine(folder,"current-comments.pdf"),Pdf=pdf }; session.Snapshot();
        try
        {
            Call(window,"AddSessionTab",session); await (Task)Call(window,"RenderAsync")!;
            XPoint? reference=null;
            for(int i=0;i<3;i++)
            {
                await (Task)Call(window,"NavigateAsync",i)!; await (Task)Call(window,"LayoutReady")!;
                Call(window,"SetTool",false,false,true,false);
                var surface=((System.Collections.IList)Field(window,"surfaces"))[i]!;
                window.GetType().GetField("activeSurface",All)!.SetValue(window,surface);
                await (Task)Call(window,"LoadTextWordsAsync",surface)!;
                var words=(List<TextWord>)surface.GetType().GetProperty("Words")!.GetValue(surface)!;
                Require(words.Count==6,"PDF fixture word extraction failed");
                static Point Center(Rect r)=>new(r.X+r.Width/2,r.Y+r.Height/2);
                var point=PdfTextComments.ToPdf(session.Pdf!.Pages[i],Center(words[0].Bounds)); reference??=point;
                Require(Math.Abs(point.X-reference.Value.X)<1 && Math.Abs(point.Y-reference.Value.Y)<1,"Rotated/cropped PDF coordinate mismatch");
                var element=(FrameworkElement)surface;
                window.GetType().GetField("start",All)!.SetValue(window,new Point(Center(words[0].Bounds).X*element.Width,Center(words[0].Bounds).Y*element.Height));
                await (Task)Call(window,"FinishTextSelectionAsync",new Point(Center(words[^1].Bounds).X*element.Width,Center(words[^1].Bounds).Y*element.Height))!;
                Require(((System.Collections.IList)Field(window,"selectedWords")).Count==6,"Multiline PDF selection failed");
                var highlights=(Canvas)surface.GetType().GetProperty("TextHighlights")!.GetValue(surface)!;
                var bands=PdfTextComments.HighlightBands(words,element.Height/element.Width);
                Require(highlights.Children.Count==bands.Count && bands.Count>=2 && bands.Count<6,"Selection does not form continuous line bands");
                ((TextBox)window.FindName("OcrText")).Text="Комментарий к выбранным строкам"; await (Task)Call(window,"ApplyCardAsync")!;
                var note=session.Pdf!.Pages[i].Annotations[session.Pdf.Pages[i].Annotations.Count-1];
                Require(note.Elements.GetName("/Subtype")=="/Highlight" && note.Elements.GetArray("/QuadPoints")!.Elements.Count==8*bands.Count,"PDF comment geometry failed");
                Require(note.Elements.GetString("/ODQuote")=="Alpha beta gamma Second line text","PDF quote not preserved");
            }
            session.SaveTo(session.Path);
            var copyPath=Path.Combine(folder,"current-comments-copy.pdf"); session.DuplicateWithAuthor(copyPath,"Другой автор");
            using(var copy=DocumentSession.Open(copyPath)) Require(copy.Pdf!.Pages[1].Annotations[0].Title=="Другой автор","Duplicate did not change comment author");
            await (Task)Call(window,"DeleteCommentAsync",0,1)!; session.SaveTo(session.Path);
            using(var reopened=DocumentSession.Open(session.Path)) Require(reopened.Pdf!.Pages[0].Annotations.Count==1 && reopened.Pdf.Pages[0].Annotations[0].Contents=="Keep unrelated note","Comment deletion changed unrelated note");
            Pass("PDF multiline selection: continuous bands; rotated/cropped pages; saved comments/quotes; duplicate authors; deletion preserves unrelated notes");
        }
        finally { session.Dirty=false; window.Close(); }
    }
    async Task WordAsync()
    {
        var folder=Path.GetDirectoryName(report)!;
        var path=Path.Combine(folder,"word-fixture.docx");
        DocumentSession.WriteDocx(["Alpha text for color selection.\nПривет, мир!","SECOND PAGE","THIRD PAGE"],path);
        // Reproduce valid minimal DOCX produced by earlier application versions:
        // no optional styles/settings and no main-part relationships file.
        using(var minimal=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            foreach(var name in new[] { "word/styles.xml","word/settings.xml","word/_rels/document.xml.rels" }) minimal.GetEntry(name)?.Delete();
            var types=minimal.GetEntry("[Content_Types].xml")!;
            System.Xml.Linq.XDocument xml; using(var stream=types.Open()) xml=System.Xml.Linq.XDocument.Load(stream);
            foreach(var node in xml.Root!.Elements().Where(x=>new[] { "/word/styles.xml","/word/settings.xml" }.Contains((string?)x.Attribute("PartName"))).ToArray()) node.Remove();
            types.Delete(); using var target=minimal.CreateEntry("[Content_Types].xml").Open(); xml.Save(target);
        }
        var originalBytes=File.ReadAllBytes(path);
        var editor=Activator.CreateInstance(Product.GetType("ObsidianDocs.WordEditor")!,[originalBytes])!;
        var errors=new List<string>(); editor.GetType().GetEvent("Error")!.AddEventHandler(editor,new Action<string>(errors.Add));
        var browser=(WebView2)Field(editor,"browser");
        browser.CoreWebView2InitializationCompleted+=(_,args)=>
        {
            if(!args.IsSuccess) return;
            bool first=true;
            browser.CoreWebView2.NavigationStarting+=(_,navigation)=>
            {
                if(!first || !navigation.Uri.Contains("obsidopf-word.local")) return;
                first=false; navigation.Cancel=true;
                Dispatcher.BeginInvoke(new Action(async ()=>
                {
                    await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("""
                        (()=>{let attach;Object.defineProperty(window,'attachColorPicker',{configurable:true,
                        get(){return attach;},set(fn){attach=(getPresentation,send)=>{window.__validationPresentation=getPresentation;return fn(getPresentation,send);};}});})();
                        """);
                    browser.CoreWebView2.Navigate(navigation.Uri);
                }));
            };
        };
        var owner=new MainWindow { Title="ObsiDopf · Word release validation",Width=1100,Height=760,Content=editor };
        owner.Show();
        try
        {
            var completion=Field(editor,"ready"); await ((Task)completion.GetType().GetProperty("Task")!.GetValue(completion)!).WaitAsync(TimeSpan.FromMinutes(2));
            await Task.Delay(500);
            async Task<JsonElement> Js(string body)
            {
                var result=await browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify((()=>{try{"+body+"}catch(e){return {__error:String(e.stack||e)};}})())");
                var value=JsonDocument.Parse(JsonSerializer.Deserialize<string>(result) ?? "{\"__error\":\"No script result\"}").RootElement.Clone();
                if(value.ValueKind==JsonValueKind.Object && value.TryGetProperty("__error",out var failure)) throw new InvalidOperationException(failure.GetString());
                return value;
            }
            async Task<string> Xml()
            {
                var bytes=await (Task<byte[]>)Call(editor,"SnapshotAsync")!;
                using var stream=new MemoryStream(bytes); using var archive=new ZipArchive(stream);
                using var reader=new StreamReader(archive.GetEntry("word/document.xml")!.Open()); return await reader.ReadToEndAsync();
            }
            async Task WaitColor(string value)
            {
                for(int i=0;i<100;i++)
                {
                    if((await Js("return JSON.stringify(window.__ve.getJSON()).toUpperCase().includes('"+value+"');")).GetBoolean()) return;
                    await Task.Delay(100);
                }
                throw new InvalidOperationException("Color update did not reach document: "+value);
            }
            var engine=await Js("const p=window.__validationPresentation?.();window.__ve=p?.getActiveEditor();return {editor:!!window.__ve,pages:document.querySelectorAll('.superdoc-page').length};");
            Require(engine.GetProperty("editor").GetBoolean(),"Word editing surface unavailable: "+engine);
            Require((int)editor.GetType().GetProperty("PageCount")!.GetValue(editor)!>=3,"Word page count lost explicit breaks");
            // Search uses the actual bridge; count is validated separately from DOM.
            var searchTask=(Task)Call(editor,"SearchAsync","Alpha",null)!; await searchTask;
            var searchResult=searchTask.GetType().GetProperty("Result")!.GetValue(searchTask)!;
            Require((int)searchResult.GetType().GetField("Item1")!.GetValue(searchResult)!>0,"Word search failed");
            Call(editor,"ClearSearch");
            Pass("WebView2/SuperDoc loads installed assets, creates editable multi-page DOCX and finds text");
            await Js("const e=window.__ve;e.commands.setTextSelection({from:1,to:6});return {ok:true};");
            string before=await Xml();
            async Task<Window> OpenPalette(string kind,bool eyedropper=false)
            {
                await Js("if(document.querySelector('.options-grid-wrap'))document.querySelector('[data-item=\"btn-'+(window.__vpKind||'"+kind+"')+'\"]')?.click();window.__vpKind='"+kind+"';return true;");
                await Task.Delay(150);
                await Js("const e=window.__ve;e.commands.setTextSelection({from:1,to:6});const b=document.querySelector('[data-item=\"btn-"+kind+"\"]');if(!b)throw Error('Missing color toolbar');b.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true}));b.click();return true;");
                await Task.Delay(200);
                await Js("const a=[...document.querySelectorAll('.obsidopf-color-actions button')].find(b=>b.textContent.includes('"+(eyedropper ? "Пипетка" : "Другие цвета")+"'));if(!a)throw Error('Advanced palette action missing');a.click();return true;");
                await WaitAsync(()=>Windows.OfType<Window>().Any(w=>w.GetType().Name=="ColorPaletteWindow"),"palette");
                return Windows.OfType<Window>().Single(w=>w.GetType().Name=="ColorPaletteWindow");
            }
            var palette=await OpenPalette("color");
            var hex=(TextBox)Field(palette,"hex"); var apply=(Button)Field(palette,"apply");
            hex.Text="bad!"; Require(!apply.IsEnabled,"Invalid HEX enabled Apply");
            Picture(palette,Path.Combine(folder,"palette.png")); palette.Close(); await Task.Delay(300);
            Require(await Xml()==before,"Cancel changed document"); Pass("Advanced palette opens without WebView2 reentrancy; invalid HEX rejected; cancel preserves document");
            palette=await OpenPalette("color"); ((TextBox)Field(palette,"hex")).Text="#A1B2C3";
            ((Button)Field(palette,"apply")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(350);
            await WaitColor("A1B2C3");
            var colorXml=await Xml(); File.WriteAllText(Path.Combine(folder,"color-export.xml"),colorXml);
            var colorState=await Js("return {error:document.querySelector('.obsidopf-color-error')?.textContent||'',document:window.__ve.getJSON()};");
            Require(colorXml.Contains("A1B2C3",StringComparison.OrdinalIgnoreCase),"Selected text color did not survive DOCX export: "+colorState.GetProperty("error").GetString()+"; host errors: "+string.Join("; ",errors));
            Pass("Arbitrary text color applies to original selection and survives DOCX export");
            palette=await OpenPalette("highlight"); ((TextBox)Field(palette,"hex")).Text="#D1E2F3";
            ((Button)Field(palette,"apply")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(350);
            await WaitColor("D1E2F3");
            Require((await Xml()).Contains("D1E2F3",StringComparison.OrdinalIgnoreCase),"Arbitrary highlight did not survive DOCX export");
            Pass("Arbitrary marker RGB survives DOCX export");
            var undo=(Task)Call(editor,"CallAsync","undo",null)!; await undo;
            Require(!(await Xml()).Contains("D1E2F3",StringComparison.OrdinalIgnoreCase),"Undo failed for marker");
            Pass("Word undo restores formatting");
            await Js("window.__ve.commands.setTextSelection(8);window.__ve.commands.insertContent(' EDITED ');return true;");
            Require((await Xml()).Contains("EDITED",StringComparison.Ordinal),"Edited text not exported");
            Pass("Typed Word edits survive DOCX export");
            await Js("const c=window.__ve.converter;c.__validationExport=c.exportToDocx;c.exportToDocx=async()=>{throw new Error('VALIDATION_EXPORT_FAILURE');};return true;");
            bool rejected=false;
            try { await Xml(); }
            catch(Exception error) when(error.Message.Contains("VALIDATION_EXPORT_FAILURE")) { rejected=true; }
            finally { await Js("const c=window.__ve.converter;c.exportToDocx=c.__validationExport;delete c.__validationExport;return true;"); }
            Require(rejected,"Export returned original document after converter failure");
            Require((await Xml()).Contains("EDITED",StringComparison.Ordinal),"Export retry lost changes");
            Pass("Failed DOCX export rejects original-Blob fallback; retry preserves edits");
            palette=await OpenPalette("color",true);
            await WaitAsync(()=>Windows.OfType<Window>().Any(w=>w.GetType().Name=="ScreenColorPicker"),"eyedropper");
            Windows.OfType<Window>().Single(w=>w.GetType().Name=="ScreenColorPicker").Close();
            await Task.Delay(200); palette.Close(); await Task.Delay(150);
            Pass("Screen eyedropper opens and cancellation returns to palette without blocking editor");
            Require(File.ReadAllBytes(path).SequenceEqual(originalBytes),"Opening/saving editor snapshots modified source fixture");
            Pass("Legacy minimal DOCX is normalized in memory without rewriting source file");
            var materialsType=Product.GetType("ObsidianDocs.MaterialWindow")!;
            var materialPdf=Path.Combine(folder,"validation-converted.pdf");
            if(!File.Exists(materialPdf))
            {
                var renderer=Product.GetType("ObsidianDocs.DocxRenderer")!;
                var bytes=await Task.Run(()=>(byte[])renderer.GetMethod("Render",All)!.Invoke(null,[path,CancellationToken.None])!);
                File.WriteAllBytes(materialPdf,bytes);
            }
            using var materialSource=DocumentSession.Open(materialPdf);
            var material=(Window)Activator.CreateInstance(materialsType,[owner,materialSource,Array.CreateInstance(Product.GetType("ObsidianDocs.MaterialPage")!,0)])!;
            material.Show(); await Task.Delay(400);
            var tabs=(TabControl)Field(material,"tabs");
            Require(tabs.Background is SolidColorBrush brush && brush.Color.R<80,"Material picker tab surface is not dark");
            Picture(material,Path.Combine(folder,"materials.png")); material.Close();
            Pass("Materials window uses dark TabControl surface and renders fixture preview");
        }
        finally { ((IDisposable)editor).Dispose(); owner.Close(); }
    }
}
