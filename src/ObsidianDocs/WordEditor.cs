using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ObsidianDocs;

// Word uses an actual paginated editing model. The local WebView hosts the
// bundled engine; neither document bytes nor rendering assets leave this PC.
internal sealed class WordEditor : Grid,IDisposable
{
    const string Origin="https://obsidopf-word.local";
    static readonly ConcurrentDictionary<string,byte[]> assetCache=new();
    readonly WebView2 browser=new() { DefaultBackgroundColor=System.Drawing.Color.FromArgb(30,30,36) };
    readonly byte[] original;
    ColorPaletteWindow? colorPalette;
    readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Dictionary<int,TaskCompletionSource<JsonElement>> requests=[];
    readonly ObservableCollection<string> pageItems=[];
    readonly ListBox pageList;
    bool started,disposed;
    int nextRequest;
    Exception? processError;
    double requestedZoom=1;
    readonly string profilePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ObsidianDocs","Diagnostics",$"word-layout-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
    readonly Queue<JsonElement> layoutSamples=[];
    Task profileWrite=Task.CompletedTask;
    public event Action? Changed;
    public event Action? PagesChanged;
    public event Action<string,bool>? Shortcut;
    public event Action<double>? ZoomDelta;
    public event Action<double>? ZoomChanged;
    public event Action<string>? Error;
    public event Action<string,string>? AskAi;
    public FrameworkElement View=>this;
    public FrameworkElement Tools { get; }=new Border { Visibility=Visibility.Collapsed };
    public FrameworkElement MiniMap=>pageList;
    public int PageCount { get; private set; }=1;
    public long Revision { get; private set; }
    public WordEditor(byte[] bytes)
    {
        original=bytes;
        Background=new SolidColorBrush(Color.FromRgb(30,30,36));
        Children.Add(browser);
        pageList=new ListBox
        {
            ItemsSource=pageItems,BorderThickness=new Thickness(0),
            Background=Background,Foreground=new SolidColorBrush(Color.FromRgb(221,215,233)),
            Padding=new Thickness(8),HorizontalContentAlignment=HorizontalAlignment.Stretch
        };
        VirtualizingPanel.SetIsVirtualizing(pageList,true);
        VirtualizingPanel.SetVirtualizationMode(pageList,VirtualizationMode.Recycling);
        pageList.SelectionChanged+=async (_,_)=>
        {
            if(pageList.SelectedIndex<0) return;
            try { await CallAsync("page",new { page=pageList.SelectedIndex }); }
            catch(Exception error) { Error?.Invoke(error.Message); }
        };
        pageItems.Add("Страница 1");
        Loaded+=async (_,_)=>
        {
            if(started || disposed) return;
            started=true;
            try { await InitializeAsync(); }
            catch(Exception error)
            {
                ready.TrySetException(error);
                Children.Clear();
                Children.Add(new TextBlock { Text="Не удалось открыть редактор Word.\n"+error.Message+"\nДля этого режима нужен Microsoft Edge WebView2 Runtime.",Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(30) });
                Error?.Invoke(error.Message);
            }
        };
    }
    async Task InitializeAsync()
    {
        var assets=Path.Combine(AppContext.BaseDirectory,"WordAssets");
        if(!File.Exists(Path.Combine(assets,"index.html"))) throw new FileNotFoundException("Не собраны компоненты редактора: выполните npm ci и npm run build в web/word.");
        var profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ObsidianDocs","WordWebView");
        var environment=await CoreWebView2Environment.CreateAsync(null,profile);
        if(disposed) return;
        await browser.EnsureCoreWebView2Async(environment);
        if(disposed) return;
        var core=browser.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled=false;
        core.Settings.AreDevToolsEnabled=false;
        core.Settings.IsStatusBarEnabled=false;
        core.Settings.IsZoomControlEnabled=false;
        // A mapped virtual hostname bypasses WebResourceRequested. Serve both
        // assets and the in-memory document through this handler instead.
        var resources=new Dictionary<string,(string File,string Mime)>
        {
            ["/index.html"]=("index.html","text/html; charset=utf-8"),
            ["/editor.js"]=("editor.js","application/javascript; charset=utf-8"),
            ["/layout-performance.js"]=("layout-performance.js","application/javascript; charset=utf-8"),
            ["/selection-assistant.js"]=("selection-assistant.js","application/javascript; charset=utf-8"),
            ["/color-picker.js"]=("color-picker.js","application/javascript; charset=utf-8"),
            ["/editor.css"]=("editor.css","text/css; charset=utf-8"),
            ["/vendor/superdoc.min.js"]=("vendor/superdoc.min.js","application/javascript; charset=utf-8"),
            ["/vendor/style.css"]=("vendor/style.css","text/css; charset=utf-8")
        };
        core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
        core.WebResourceRequested+=(_,e)=>
        {
            var uri=new Uri(e.Request.Uri);
            if(uri.Scheme is "data" or "blob") return;
            if(uri.GetLeftPart(UriPartial.Authority)!=Origin)
            {
                e.Response=environment.CreateWebResourceResponse(null,403,"Local editor only",""); return;
            }
            if(uri.AbsolutePath=="/document.docx")
            {
                e.Response=environment.CreateWebResourceResponse(new MemoryStream(original,false),200,"OK","Content-Type: application/vnd.openxmlformats-officedocument.wordprocessingml.document\r\nCache-Control: no-store");
                return;
            }
            if(resources.TryGetValue(uri.AbsolutePath,out var resource))
            {
                var bytes=assetCache.GetOrAdd(Path.Combine(assets,resource.File),File.ReadAllBytes);
                e.Response=environment.CreateWebResourceResponse(new MemoryStream(bytes,false),200,"OK","Content-Type: "+resource.Mime);
            }
            else e.Response=environment.CreateWebResourceResponse(null,404,"Not found","");
        };
        core.NavigationStarting+=(_,e)=> { if(!e.Uri.StartsWith(Origin+"/",StringComparison.Ordinal)) e.Cancel=true; };
        core.NewWindowRequested+=(_,e)=>e.Handled=true;
        core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
        core.DownloadStarting+=(_,e)=>e.Cancel=true;
        core.WebMessageReceived+=MessageReceived;
        core.ProcessFailed+=(_,e)=>
        {
            var error=new InvalidOperationException("Процесс редактора остановлен: "+e.ProcessFailedKind);
            processError=error;
            ready.TrySetException(error);
            foreach(var request in requests.Values) request.TrySetException(error);
            requests.Clear(); Error?.Invoke(error.Message);
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync("window.obsidopfConfig="+JsonSerializer.Serialize(new
        {
            user=Environment.UserName,
            profileLayout=Environment.GetEnvironmentVariable("OBSIDOPF_PROFILE_LAYOUT")=="1",
            fonts=Fonts.SystemFontFamilies.Select(f=>f.Source).Distinct().OrderBy(n=>n).ToArray()
        })+";");
        if(disposed) return;
        core.Navigate(Origin+"/index.html");
    }
    void MessageReceived(object? sender,CoreWebView2WebMessageReceivedEventArgs e)
    {
        if(disposed || !e.Source.StartsWith(Origin+"/",StringComparison.Ordinal)) return;
        try
        {
            using var message=JsonDocument.Parse(e.WebMessageAsJson);
            var data=message.RootElement;
            switch(data.GetProperty("type").GetString())
            {
                case "ready": ready.TrySetResult(); SetZoom(requestedZoom); break;
                case "askGemini": AskAi?.Invoke(data.GetProperty("text").GetString() ?? "",data.GetProperty("prompt").GetString() ?? ""); break;
                case "pickColor":
                {
                    string id=data.GetProperty("id").GetString() ?? "";
                    string initial=data.GetProperty("color").GetString() ?? "#000000";
                    bool highlight=data.GetProperty("kind").GetString()=="highlight",eyedropper=data.GetProperty("eyedropper").GetBoolean();
                    // Complete the WebView2 callback before starting a modal
                    // WPF message loop; WebView2 does not support reentrancy.
                    Dispatcher.BeginInvoke(new Action(()=>PickColorFromToolbar(id,initial,highlight,eyedropper)));
                    break;
                }
                case "layoutProfile":
                    foreach(var sample in data.GetProperty("samples").EnumerateArray())
                    {
                        layoutSamples.Enqueue(sample.Clone());
                        while(layoutSamples.Count>60) layoutSamples.Dequeue();
                    }
                    // Never queue disk work on every key press or block the UI.
                    if(profileWrite.IsCompleted)
                    {
                        var json=JsonSerializer.Serialize(new { engine="SuperDoc 1.47.2",pages=PageCount,samples=layoutSamples.ToArray() });
                        profileWrite=Task.Run(async ()=>
                        {
                            try
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
                                await File.WriteAllTextAsync(profilePath+".tmp",json);
                                File.Move(profilePath+".tmp",profilePath,true);
                            }
                            catch(IOException) { }
                            catch(UnauthorizedAccessException) { }
                        });
                    }
                    break;
                case "changed":
                    long revision=data.GetProperty("revision").GetInt64();
                    if(revision<=Revision) break;
                    Revision=revision; Changed?.Invoke(); break;
                case "pages":
                    if(!data.GetProperty("count").TryGetInt32(out int reportedCount)) break;
                    int count=Math.Max(1,reportedCount);
                    if(count==PageCount) break;
                    PageCount=count;
                    while(pageItems.Count<count) pageItems.Add($"Страница {pageItems.Count+1}");
                    while(pageItems.Count>count) pageItems.RemoveAt(pageItems.Count-1);
                    PagesChanged?.Invoke(); break;
                case "shortcut": Shortcut?.Invoke(data.GetProperty("key").GetString()!,data.GetProperty("shift").GetBoolean()); break;
                case "zoomDelta": ZoomDelta?.Invoke(data.GetProperty("delta").GetDouble()); break;
                case "zoom": ZoomChanged?.Invoke(data.GetProperty("value").GetDouble()); break;
                case "error":
                    string text=data.GetProperty("message").GetString() ?? "Ошибка редактора";
                    if(!ready.Task.IsCompleted) ready.TrySetException(new InvalidDataException(text));
                    Error?.Invoke(text); break;
                case "reply":
                    if(!requests.Remove(data.GetProperty("id").GetInt32(),out var completion)) break;
                    if(data.TryGetProperty("error",out var error)) completion.TrySetException(new InvalidOperationException(error.GetString()));
                    else completion.TrySetResult(data.GetProperty("result").Clone());
                    break;
            }
        }
        catch(JsonException error) { Error?.Invoke("Ошибка сообщения редактора: "+error.Message); }
    }
    void PickColorFromToolbar(string id,string initial,bool highlight,bool eyedropper)
    {
        if(disposed) return;
        bool accepted=false; string? value=null,paletteError=null;
        ColorPaletteWindow? palette=null;
        try
        {
            if(colorPalette is not null) throw new InvalidOperationException("Палитра уже открыта.");
            if(IsVisible && Window.GetWindow(this) is { } owner)
            {
                if(!ColorPaletteWindow.TryHex(initial,out var color)) color=Colors.Black;
                palette=new ColorPaletteWindow(owner,color,highlight,eyedropper); colorPalette=palette;
                accepted=palette.ShowDialog()==true;
                if(accepted && palette.SelectedColor is { } selectedColor) value=ColorPaletteWindow.Hex(selectedColor);
            }
        }
        catch(Exception failure) { paletteError=failure.Message; }
        finally { if(ReferenceEquals(colorPalette,palette)) colorPalette=null; }
        if(disposed || processError is not null) return;
        try { browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type="colorPicked",id,accepted,color=value,error=paletteError })); }
        catch(Exception failure) when(failure is InvalidOperationException or System.Runtime.InteropServices.COMException) { if(!disposed) Error?.Invoke(failure.Message); }
    }
    async Task<JsonElement> CallAsync(string command,object? data=null)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(processError is { } failure) throw failure;
        await ready.Task.WaitAsync(TimeSpan.FromMinutes(2));
        ObjectDisposedException.ThrowIf(disposed,this);
        int id=++nextRequest;
        var response=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests[id]=response;
        try
        {
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id,command,data }));
            return await response.Task.WaitAsync(TimeSpan.FromMinutes(2));
        }
        finally { requests.Remove(id); }
    }
    public async Task<byte[]> SnapshotAsync()
    {
        var result=await CallAsync("snapshot");
        return Convert.FromBase64String(result.GetProperty("base64").GetString()!);
    }
    public async Task<string> SelectedTextAsync()
    {
        var result=await CallAsync("selection");
        return result.GetProperty("text").GetString() ?? "";
    }
    public async Task<(int Total,int Index)> SearchAsync(string query,int? direction=null)
    {
        var result=direction is { } step ? await CallAsync("searchStep",new { direction=step }) : await CallAsync("search",new { query });
        return (result.GetProperty("total").GetInt32(),result.GetProperty("index").GetInt32());
    }
    public async void ClearSearch()
    {
        if(disposed || !ready.Task.IsCompletedSuccessfully) return;
        try { await CallAsync("clearSearch"); } catch(Exception error) { Error?.Invoke(error.Message); }
    }
    public async void SetZoom(double value)
    {
        requestedZoom=value;
        if(disposed || !ready.Task.IsCompletedSuccessfully) return;
        try { await CallAsync("zoom",new { value }); } catch(Exception error) { Error?.Invoke(error.Message); }
    }
    public new async void Focus()
    {
        browser.Focus();
        if(disposed || !ready.Task.IsCompletedSuccessfully) return;
        try { await CallAsync("focus"); } catch(Exception error) { Error?.Invoke(error.Message); }
    }
    public void Dispose()
    {
        if(disposed) return;
        disposed=true; colorPalette?.Close(); colorPalette=null; ready.TrySetCanceled();
        foreach(var request in requests.Values) request.TrySetCanceled();
        requests.Clear(); browser.Dispose();
    }
}
