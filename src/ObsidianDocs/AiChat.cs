using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Shapes;
using System.IO;
using System.Windows.Threading;
using PdfSharp.Pdf.IO;

namespace ObsidianDocs;
public partial class MainWindow
{
    readonly List<GeminiClient.Turn> chatHistory=[];
    readonly List<MaterialPage> chatMaterials=[];
    void RefreshMaterialHint()=>MaterialHint.Text=chatMaterials.Count==0 ? "" : $"Изображений: {chatMaterials.Count} · нажмите «Выбрать материал», чтобы просмотреть и изменить";
    void ClearMaterial_Click(object sender,RoutedEventArgs e) { chatMaterials.Clear(); RefreshMaterialHint(); }
    async void ChooseMaterial_Click(object sender,RoutedEventArgs e)
    {
        if(busy || chatCancellation is not null) return;
        var session=current;
        DocumentSession? owned=null;
        try
        {
            var source=session;
            if(session?.IsWordDocument==true && session.Editor is { } editor)
            {
                SetBusy(true,"Готовлю страницы DOCX для выбора материала…"); DocxHost.IsEnabled=false;
                try
                {
                    var bytes=await editor.SnapshotAsync();
                    var pdf=await Task.Run(()=>RenderDocxBytes(bytes));
                    owned=new DocumentSession { Path="Страницы DOCX",RenderBytes=pdf,Pdf=PdfReader.Open(new MemoryStream(pdf),PdfDocumentOpenMode.Modify) };
                    source=owned;
                }
                finally { DocxHost.IsEnabled=true; SetBusy(false,"Материал для Gemini"); }
            }
            var window=new MaterialWindow(this,source,chatMaterials);
            if(window.ShowDialog()==true) { chatMaterials.Clear(); chatMaterials.AddRange(window.Result); RefreshMaterialHint(); }
        }
        catch(Exception error) { ChatStatus.Text="Не удалось подготовить материал: "+error.Message; }
        finally { owned?.Dispose(); }
    }
    string chatSelection="";
    CancellationTokenSource? chatCancellation;
    int chatGeneration;
    Popup? aiSelectionPopup;
    void ConfigureAiChat()
    {
        Viewer.PreviewMouseWheel+=(_,_)=>HideAiSelectionPopup();
        Deactivated+=(_,_)=>HideAiSelectionPopup();
        Closed+=(_,_)=>chatCancellation?.Cancel();
    }
    void SetAiChatVisible(bool visible)
    {
        AiChatPanel.Visibility=visible ? Visibility.Visible : Visibility.Collapsed;
        if(visible) RecentFilesPanel.Visibility=Visibility.Collapsed;
        UpdateLeftPanelWidth();
    }
    void ToggleAiChat_Click(object sender,RoutedEventArgs e)=>SetAiChatVisible(AiChatPanel.Visibility!=Visibility.Visible);
    void CloseAiChat_Click(object sender,RoutedEventArgs e)=>SetAiChatVisible(false);
    void SetChatSelection(string selected,AiCommentTarget? target=null)
    {
        chatCommentTarget=target;
        chatSelection=selected;
        ChatContext.Visibility=string.IsNullOrEmpty(selected) ? Visibility.Collapsed : Visibility.Visible;
        ChatComment.IsEnabled=target is not null;
        ChatComment.Visibility=target is null ? Visibility.Collapsed : Visibility.Visible;
        ChatComment.ToolTip="Автор комментария: "+authors.Selected;
        if(target is null) ChatComment.IsChecked=false;
    }
    void ClearChatContext_Click(object sender,RoutedEventArgs e)=>SetChatSelection("");
    void NewAiChat_Click(object sender,RoutedEventArgs e)
    {
        chatGeneration++; chatCancellation?.Cancel(); chatHistory.Clear(); ChatMessages.Children.Clear();
        chatMaterials.Clear(); RefreshMaterialHint();
        SetChatSelection(""); ChatStatus.Text="Новый чат · можно задать любой вопрос";
    }
    void ChatCancel_Click(object sender,RoutedEventArgs e)=>chatCancellation?.Cancel();
    async void ChatSend_Click(object sender,RoutedEventArgs e)=>await SendChatAsync(ChatPrompt.Text,chatSelection);
    async void ChatPrompt_KeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Enter && Keyboard.Modifiers==ModifierKeys.Control) { e.Handled=true; await SendChatAsync(ChatPrompt.Text,chatSelection); }
    }
    void AddChatMessage(string title,string text,bool user)
    {
        var stack=new StackPanel();
        stack.Children.Add(new TextBlock { Text=title,FontSize=11,Foreground=Brush("#BCA8DC"),Margin=new Thickness(0,0,0,7) });
        stack.Children.Add(new TextBox { Text=text,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,
            Background=Brushes.Transparent,Foreground=Brush("#E8E5F0"),BorderThickness=new Thickness(0),Padding=new Thickness(0),MaxHeight=1100,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto });
        var copy=new Button { Content="Копировать",FontSize=11,HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(5,3,5,3),Margin=new Thickness(0,6,0,0) };
        copy.Click+=(_,_)=> { try { Clipboard.SetText(text); } catch(Exception error) { ChatStatus.Text=error.Message; } };
        stack.Children.Add(copy);
        ChatMessages.Children.Add(new Border { Child=stack,Padding=new Thickness(12),CornerRadius=new CornerRadius(10),
            Background=Brush(user ? "#3A304B" : "#2B2935"),Margin=new Thickness(user ? 20 : 0,8,user ? 0 : 12,4) });
        ChatScroll.ScrollToEnd();
    }
    async Task SendChatAsync(string prompt,string selected,AiCommentTarget? commentTarget=null,bool? comment=null)
    {
        prompt=prompt.Trim();
        SetAiChatVisible(true);
        if(chatCancellation is not null) { ChatStatus.Text="Дождитесь ответа или нажмите «Стоп»."; return; }
        if(prompt.Length==0) { ChatStatus.Text="Напишите вопрос."; return; }
        var target=commentTarget ?? chatCommentTarget;
        bool addComment=comment ?? ChatComment.IsChecked==true;
        if(addComment && target is null) { ChatStatus.Text="Для комментария сначала выделите текст в документе."; return; }
        string author=authors.Selected;
        var settings=GeminiSettings.Load();
        if(string.IsNullOrWhiteSpace(settings.Key))
        {
            new AiWindow(this,settingsOnly:true).ShowDialog(); settings=GeminiSettings.Load();
            if(string.IsNullOrWhiteSpace(settings.Key)) { ChatStatus.Text="Укажите ключ в настройках API ⚙."; return; }
        }
        using var token=new CancellationTokenSource(); chatCancellation=token;
        int generation=chatGeneration;
        ChatSend.IsEnabled=false; ChatCancel.IsEnabled=true; ChatStatus.Text="Gemini отвечает…";
        var materials=chatMaterials.Select(p=>p.Copy()).ToList();
        ChooseMaterial.IsEnabled=false;
        try
        {
            var attachments=new List<GeminiClient.ImageInput>();
            foreach(var page in materials)
            {
                token.Token.ThrowIfCancellationRequested();
                ChatStatus.Text=$"Подготавливаю изображение {attachments.Count+1} из {materials.Count}…";
                attachments.Add(await Dispatcher.InvokeAsync(()=>MaterialWindow.Encode(page),DispatcherPriority.Background));
            }
            if(generation!=chatGeneration) return;
            AddChatMessage("Вы"+(string.IsNullOrEmpty(selected) ? "" : " · с выделением")+(materials.Count==0 ? "" : $" · изображений: {materials.Count}"),prompt,true);
            if(chatCommentTarget==target) chatCommentTarget=null; // keep the source selection snapshot with this request
            ChatPrompt.Clear(); SetChatSelection(""); chatMaterials.Clear(); RefreshMaterialHint(); HideAiSelectionPopup(); ChatStatus.Text="Gemini отвечает…";
            var result=await GeminiClient.GenerateAsync(settings.Key,settings.Settings.Model,selected,prompt,token.Token,chatHistory.ToArray(),attachments);
            if(generation!=chatGeneration) return;
            chatHistory.Add(new("user",GeminiClient.UserText(selected,prompt),attachments.ToArray())); chatHistory.Add(new("model",result));
            while(chatHistory.Count>20) chatHistory.RemoveRange(0,2);
            AddChatMessage("Gemini",result,false); ChatStatus.Text="Готово";
            if(addComment && target is not null)
            {
                try { await AddAiReplyCommentAsync(target,result,author); ChatStatus.Text=$"Ответ добавлен в комментарий от «{author}» · Ctrl+S — сохранить"; }
                catch(Exception error) { ChatStatus.Text="Ответ в чате. Комментарий не добавлен: "+error.Message; }
            }
            target=null;
        }
        catch(OperationCanceledException)
        {
            if(generation==chatGeneration) { ChatStatus.Text=token.IsCancellationRequested ? "Запрос остановлен." : "Истекло время ожидания."; ChatPrompt.Text=prompt; SetChatSelection(selected,target); ChatComment.IsChecked=addComment; target=null; chatMaterials.Clear(); chatMaterials.AddRange(materials); RefreshMaterialHint(); }
        }
        catch(Exception error)
        {
            if(generation==chatGeneration) { ChatStatus.Text=error.Message; ChatPrompt.Text=prompt; SetChatSelection(selected,target); ChatComment.IsChecked=addComment; target=null; chatMaterials.Clear(); chatMaterials.AddRange(materials); RefreshMaterialHint(); }
        }
        finally { chatCancellation=null; ChatSend.IsEnabled=ChooseMaterial.IsEnabled=true; ChatCancel.IsEnabled=false; }
    }
    void HideAiSelectionPopup() { if(aiSelectionPopup is not null) aiSelectionPopup.IsOpen=false; }
    void ShowAiSelectionPopup(string text,Point end)
    {
        HideAiSelectionPopup();
        if(string.IsNullOrWhiteSpace(text)) return;
        var commentTarget=CapturePdfAiTarget();
        var popup=new Popup { PlacementTarget=Workspace,Placement=PlacementMode.Relative,StaysOpen=true,AllowsTransparency=true,Resources=Resources };
        aiSelectionPopup=popup;
        popup.HorizontalOffset=Math.Clamp(end.X+10,0,Math.Max(0,Workspace.ActualWidth-46));
        popup.VerticalOffset=Math.Clamp(end.Y+10,0,Math.Max(0,Workspace.ActualHeight-46));
        var icon=new System.Windows.Shapes.Path { Width=21,Height=21,Stretch=Stretch.Uniform,
            Data=Geometry.Parse("M12,0 C13.8,7.2 16.8,10.2 24,12 C16.8,13.8 13.8,16.8 12,24 C10.2,16.8 7.2,13.8 0,12 C7.2,10.2 10.2,7.2 12,0Z"),
            Fill=new LinearGradientBrush(Color.FromRgb(83,169,255),Color.FromRgb(170,105,232),45) };
        var button=new Button { Content=icon,Width=38,Height=38,Padding=new Thickness(8),ToolTip="Спросить Gemini по выделению",
            Template=(ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Background='#302A3C' BorderBrush='#9F83CF' BorderThickness='1' CornerRadius='19'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border></ControlTemplate>") };
        popup.Child=button;
        button.Click+=(_,_)=>
        {
            popup.StaysOpen=false;
            popup.HorizontalOffset=Math.Min(popup.HorizontalOffset,Math.Max(0,Workspace.ActualWidth-330));
            popup.VerticalOffset=Math.Min(popup.VerticalOffset,Math.Max(0,Workspace.ActualHeight-230));
            var panel=new StackPanel();
            panel.Children.Add(new TextBlock { Text="✦ Gemini · вопрос по выделению",Foreground=Brush("#C7B3EA"),Margin=new Thickness(0,0,0,9) });
            var question=new TextBox { Height=80,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxLength=20_000,Padding=new Thickness(7) };
            panel.Children.Add(question);
            var commentBox=new CheckBox { Content="Ответ в комментарий",IsEnabled=commentTarget is not null,Foreground=Brush("#D8C9EE"),Margin=new Thickness(0,8,0,0),ToolTip="Автор: "+authors.Selected };
            panel.Children.Add(commentBox);
            var send=new Button { Content="Отправить ↑",HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0) }; panel.Children.Add(send);
            async Task Submit() { popup.IsOpen=false; await SendChatAsync(question.Text,text,commentTarget,commentBox.IsChecked==true); }
            send.Click+=async (_,_)=>await Submit();
            question.PreviewKeyDown+=async (_,e)=> { if(e.Key==Key.Enter && Keyboard.Modifiers==ModifierKeys.Control) { e.Handled=true; await Submit(); } };
            popup.Child=new Border { Child=panel,Width=320,Background=Brush("#2D2837"),BorderBrush=Brush("#9F83CF"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(13) };
            question.Focus();
        };
        popup.IsOpen=true;
    }
}
