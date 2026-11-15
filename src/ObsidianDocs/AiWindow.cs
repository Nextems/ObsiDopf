using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ObsidianDocs;

internal sealed class AiWindow : Window
{
    CancellationTokenSource? cancellation;
    public AiWindow(Window owner,string selection="",bool settingsOnly=false)
    {
        Owner=owner; Title="Gemini · выделенный текст"; Width=780; Height=850; MinWidth=570; MinHeight=580;
        Resources=owner.Resources;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.FromRgb(37,37,45)); Foreground=Brushes.White;
        ShowInTaskbar=false;
        if(settingsOnly) { Title="Настройки API · Gemini"; Height=430; MinHeight=330; }
        SourceInitialized+=(_,_)=>WindowTheme.Apply(this);
        var stack=new StackPanel { Margin=new Thickness(20) };
        Content=new ScrollViewer { Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        void Label(string text)=>stack.Children.Add(new TextBlock { Text=text,Margin=new Thickness(0,10,0,5),TextWrapping=TextWrapping.Wrap });
        TextBox Box(string text,double height,bool readOnly=false)
        {
            var box=new TextBox { Text=text,Height=height,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,IsReadOnly=readOnly,
                Padding=new Thickness(9),VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
            stack.Children.Add(box); return box;
        }
        Label(settingsOnly ? "Gemini · API-ключ и модель. Настройки доступны без открытого документа."
            : "В Google Gemini отправляются только показанный фрагмент и твой запрос — после нажатия «Отправить».");
        var saved=GeminiSettings.Load();
        Label("API-ключ Gemini");
        var key=new PasswordBox { Password=saved.Key,Padding=new Thickness(9),Background=new SolidColorBrush(Color.FromRgb(48,48,57)),Foreground=Brushes.White,BorderBrush=Brushes.DimGray };
        stack.Children.Add(key);
        var remember=new CheckBox { Content="Сохранить ключ для этого пользователя Windows",IsChecked=settingsOnly || saved.Key.Length>0,Margin=new Thickness(0,8,0,0),Foreground=Brushes.White };
        stack.Children.Add(remember);
        Label("Модель");
        var model=Box(saved.Settings.Model,34); model.AcceptsReturn=false;
        var saveSettings=new Button { Content="Сохранить настройки",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,6,0,0) };
        stack.Children.Add(saveSettings);
        if(settingsOnly)
        {
            var settingsMessage=new TextBlock { Margin=new Thickness(0,12,0,0),TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.FromRgb(185,169,212)) };
            stack.Children.Add(settingsMessage);
            var check=new Button { Content="Проверить ИИ",HorizontalAlignment=HorizontalAlignment.Left,ToolTip="Отправить короткий тестовый запрос с текущими ключом и моделью" };
            stack.Children.Add(check);
            CancellationTokenSource? checking=null;
            Closed+=(_,_)=>checking?.Cancel();
            check.Click+=async (_,_)=>
            {
                using var token=new CancellationTokenSource(); checking=token;
                check.IsEnabled=false; settingsMessage.Text="Проверяю Gemini…";
                try
                {
                    var result=await GeminiClient.GenerateAsync(key.Password.Trim(),model.Text.Trim(),"","Ответь одним словом: OK",token.Token);
                    if(IsVisible) settingsMessage.Text="ИИ отвечает: "+result;
                }
                catch(Exception error) { if(IsVisible) settingsMessage.Text="Проверка не пройдена: "+error.Message; }
                finally { checking=null; if(IsVisible) check.IsEnabled=true; }
            };
            saveSettings.Click+=(_,_)=>
            {
                try
                {
                    GeminiSettings.Save(model.Text.Trim(),key.Password.Trim(),remember.IsChecked==true);
                    settingsMessage.Text=remember.IsChecked==true ? "Настройки сохранены. Ключ защищён для текущего пользователя Windows."
                        : "Модель сохранена. Ключ доступен только до закрытия приложения.";
                }
                catch(Exception error) { settingsMessage.Text="Не удалось сохранить настройки: "+error.Message; }
            };
            var close=new Button { Content="Закрыть",HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0),IsCancel=true };
            close.Click+=(_,_)=>Close(); stack.Children.Add(close);
            return;
        }
        Label("Выделенный текст");
        var fragment=Box(selection,130,true);
        Label("Запрос");
        var prompt=Box("",90); prompt.MaxLength=20_000;
        var controls=new StackPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(0,10,0,0) }; stack.Children.Add(controls);
        var send=new Button { Content="Отправить в Gemini" }; controls.Children.Add(send);
        var cancel=new Button { Content="Остановить",IsEnabled=false }; controls.Children.Add(cancel);
        var status=new TextBlock { TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0),Foreground=new SolidColorBrush(Color.FromRgb(185,169,212)) }; stack.Children.Add(status);
        Label("Ответ");
        var answer=Box("",230,true);
        var copy=new Button { Content="Скопировать ответ",HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=false }; stack.Children.Add(copy);
        saveSettings.Click+=(_,_)=>
        {
            try { GeminiSettings.Save(model.Text.Trim(),key.Password.Trim(),remember.IsChecked==true); status.Text="Настройки сохранены."; }
            catch(Exception error) { status.Text="Не удалось сохранить настройки: "+error.Message; }
        };
        copy.Click+=(_,_)=> { try { Clipboard.SetText(answer.Text); status.Text="Ответ скопирован."; } catch(Exception error) { status.Text=error.Message; } };
        cancel.Click+=(_,_)=>cancellation?.Cancel();
        Closed+=(_,_)=>cancellation?.Cancel();
        send.Click+=async (_,_)=>
        {
            if(cancellation is not null) return;
            using var requestCancellation=new CancellationTokenSource(); cancellation=requestCancellation;
            send.IsEnabled=saveSettings.IsEnabled=false; cancel.IsEnabled=true; status.Text="Gemini отвечает…";
            try
            {
                GeminiSettings.Save(model.Text.Trim(),key.Password.Trim(),remember.IsChecked==true);
                string result=await GeminiClient.GenerateAsync(key.Password.Trim(),model.Text.Trim(),fragment.Text,prompt.Text.Trim(),requestCancellation.Token);
                if(!IsVisible) return;
                answer.Text=result; copy.IsEnabled=true; status.Text="Ответ получен.";
            }
            catch(OperationCanceledException) { if(IsVisible) status.Text=requestCancellation.IsCancellationRequested ? "Запрос остановлен." : "Превышено время ожидания Gemini."; }
            catch(Exception error) { if(IsVisible) status.Text=error.Message; }
            finally { cancellation=null; if(IsVisible) { send.IsEnabled=saveSettings.IsEnabled=true; cancel.IsEnabled=false; } }
        };
    }
}

public partial class MainWindow
{
    void AiSettings_Click(object sender,RoutedEventArgs e)
    {
        if(!busy) new AiWindow(this,settingsOnly:true).ShowDialog();
    }
    async void Ai_Click(object sender,RoutedEventArgs e)
    {
        if(busy || current is null) return;
        var session=current;
        try
        {
            string selected;
            AiCommentTarget? target;
            if(session.IsWordDocument && session.Editor is { } editor)
            {
                selected=await editor.SelectedTextAsync(); target=null;
            }
            else { selected=string.Join(" ",selectedWords.Select(w=>w.Text)); target=CapturePdfAiTarget(); }
            if(current!=session) return;
            SetAiChatVisible(true);
            SetChatSelection(selected,target);
            ChatPrompt.Focus();
        }
        catch(Exception error) { Error(error); }
    }
}
