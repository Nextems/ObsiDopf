using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ObsidianDocs;

public partial class MainWindow
{
    TabItem? movingTab;
    TabItem[] tabDragOrder=[];
    readonly Dictionary<TabItem,double> tabDragLeft=[];
    readonly Dictionary<TabItem,TranslateTransform> tabTranslations=[];
    double tabGrab;
    Point tabPress;
    int tabDropIndex=-1;
    bool tabDragActive,reorderingTabs;
    DispatcherTimer? tabEdgeTimer;
    ScrollViewer? tabStripScroller;
    FrameworkElement? tabDragPanel;

    void ConfigureTabReordering()
    {
        Tabs.PreviewMouseLeftButtonDown+=TabDragStart;
        Tabs.PreviewMouseMove+=(_,e)=>
        {
            if(movingTab is null) return;
            if(e.LeftButton!=MouseButtonState.Pressed) { FinishTabDrag(false); return; }
            if(!tabDragActive && Math.Abs(e.GetPosition(Tabs).X-tabPress.X)<SystemParameters.MinimumHorizontalDragDistance) return;
            if(!tabDragActive)
            {
                tabDragActive=true; movingTab.Opacity=.9; Panel.SetZIndex(movingTab,10);
                Mouse.OverrideCursor=Cursors.SizeWE; tabEdgeTimer?.Start();
            }
            UpdateTabDrag(); e.Handled=true;
        };
        Tabs.PreviewMouseLeftButtonUp+=(_,e)=>
        {
            if(movingTab is null) return;
            var clicked=movingTab; bool wasDrag=tabDragActive;
            FinishTabDrag(wasDrag);
            if(!wasDrag && Tabs.Items.Contains(clicked)) Tabs.SelectedItem=clicked;
            e.Handled=true;
        };
        Tabs.LostMouseCapture+=(_,_)=> { if(movingTab is not null && !Tabs.IsMouseCaptured) FinishTabDrag(false); };
        Tabs.IsEnabledChanged+=(_,_)=> { if(!Tabs.IsEnabled) FinishTabDrag(false); };
        Deactivated+=(_,_)=>FinishTabDrag(false);
        PreviewKeyDown+=(_,e)=> { if(e.Key==Key.Escape && movingTab is not null) { FinishTabDrag(false); e.Handled=true; } };
        Tabs.PreviewMouseWheel+=(_,e)=>
        {
            tabStripScroller ??=Tabs.Template.FindName("TabStripScroller",Tabs) as ScrollViewer;
            if(tabStripScroller?.ScrollableWidth>0)
            { tabStripScroller.ScrollToHorizontalOffset(tabStripScroller.HorizontalOffset-e.Delta); e.Handled=true; }
        };
        tabEdgeTimer=new DispatcherTimer(DispatcherPriority.Input,Dispatcher) { Interval=TimeSpan.FromMilliseconds(16) };
        tabEdgeTimer.Tick+=(_,_)=>
        {
            if(!tabDragActive || tabStripScroller is null) return;
            double x=Mouse.GetPosition(tabStripScroller).X;
            double step=x<32 ? -12 : x>tabStripScroller.ActualWidth-32 ? 12 : 0;
            if(step==0) return;
            tabStripScroller.ScrollToHorizontalOffset(tabStripScroller.HorizontalOffset+step);
            tabStripScroller.UpdateLayout(); UpdateTabDrag();
        };
        Closed+=(_,_)=> { tabEdgeTimer?.Stop(); FinishTabDrag(false); };
    }
    void TabDragStart(object sender,MouseButtonEventArgs e)
    {
        if(busy || e.ChangedButton!=MouseButton.Left) return;
        var node=e.OriginalSource as DependencyObject;
        TabItem? tab=null;
        while(node is not null && node!=Tabs)
        {
            // The close button keeps its usual click behavior.
            if(node is ButtonBase) return;
            if(node is TabItem item) { tab=item; break; }
            node=node is Visual ? VisualTreeHelper.GetParent(node) : (node as FrameworkContentElement)?.Parent;
        }
        if(tab is null) return;
        FinishTabDrag(false);
        // Parent is the logical TabControl; layout coordinates belong to the
        // visual items panel, including when the header strip is scrolled.
        tabDragPanel=VisualTreeHelper.GetParent(tab) as FrameworkElement;
        if(tabDragPanel is null) return;
        tabDragOrder=Tabs.Items.Cast<TabItem>().ToArray();
        foreach(var item in tabDragOrder)
        {
            var transform=TabTranslation(item);
            transform.BeginAnimation(TranslateTransform.XProperty,null); transform.X=0;
        }
        movingTab=tab; tabPress=e.GetPosition(Tabs);
        tabDragLeft.Clear();
        foreach(var item in tabDragOrder) tabDragLeft[item]=TabLayoutLeft(item);
        tabGrab=Mouse.GetPosition(tabDragPanel).X-tabDragLeft[tab];
        tabDropIndex=Tabs.Items.IndexOf(tab);
        tabStripScroller=Tabs.Template.FindName("TabStripScroller",Tabs) as ScrollViewer;
        // Select on mouse-up, so reordering an inactive document does not load
        // it or rebuild the document view while the mouse is moving.
        e.Handled=true;
        if(!Tabs.CaptureMouse()) { movingTab=null; tabDragOrder=[]; tabDragLeft.Clear(); tabDragPanel=null; }
    }
    static double TabLayoutLeft(TabItem tab)=>LayoutInformation.GetLayoutSlot(tab).Left+tab.Margin.Left;
    TranslateTransform TabTranslation(TabItem tab)
    {
        if(!tabTranslations.TryGetValue(tab,out var transform))
        { transform=new TranslateTransform(); tabTranslations[tab]=transform; tab.RenderTransform=transform; }
        return transform;
    }
    void AnimateTab(TabItem tab,double target)
    {
        var transform=TabTranslation(tab);
        double from=transform.X;
        transform.BeginAnimation(TranslateTransform.XProperty,null); transform.X=target;
        if(Math.Abs(from-target)<.1 || !SystemParameters.ClientAreaAnimation) return;
        transform.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(from,target,TimeSpan.FromMilliseconds(160))
        { EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut },FillBehavior=FillBehavior.Stop });
    }
    void UpdateTabDrag()
    {
        if(movingTab is null || !tabDragActive || tabDragPanel is null) return;
        double total=tabDragOrder.Sum(t=>t.ActualWidth);
        double left=Math.Clamp(Mouse.GetPosition(tabDragPanel).X-tabGrab,0,Math.Max(0,total-movingTab.ActualWidth));
        var dragged=TabTranslation(movingTab); dragged.BeginAnimation(TranslateTransform.XProperty,null); dragged.X=left-tabDragLeft[movingTab];
        var others=tabDragOrder.Where(t=>t!=movingTab).ToList();
        // Compare insertion slots after removing the dragged item, so a wide
        // tab can still reach either end beside much narrower tabs.
        int index=0;
        double slot=0;
        foreach(var item in others)
        {
            if(left>slot+item.ActualWidth/2) index++;
            slot+=item.ActualWidth;
        }
        if(index==tabDropIndex) return;
        tabDropIndex=index; others.Insert(index,movingTab);
        double x=0;
        foreach(var item in others)
        {
            if(item!=movingTab) AnimateTab(item,x-tabDragLeft[item]);
            x+=item.ActualWidth;
        }
    }
    void FinishTabDrag(bool commit)
    {
        var tab=movingTab;
        if(tab is null) return;
        var before=tabDragOrder.ToDictionary(t=>t,t=>TabLayoutLeft(t)+TabTranslation(t).X);
        bool move=commit && tabDragActive && tabDropIndex>=0 && tabDropIndex<Tabs.Items.Count && Tabs.Items.Contains(tab);
        movingTab=null; tabDragActive=false; tabEdgeTimer?.Stop(); Mouse.OverrideCursor=null;
        if(Tabs.IsMouseCaptured) Tabs.ReleaseMouseCapture();
        tab.Opacity=1; Panel.SetZIndex(tab,0);
        if(move && Tabs.Items.IndexOf(tab)!=tabDropIndex)
        {
            var selected=Tabs.SelectedItem;
            reorderingTabs=true;
            try
            {
                Tabs.Items.Remove(tab); Tabs.Items.Insert(tabDropIndex,tab);
                Tabs.SelectedItem=selected;
                sessions.Clear(); sessions.AddRange(Tabs.Items.Cast<TabItem>().Select(t=>(DocumentSession)t.Tag));
                Tabs.UpdateLayout();
            }
            finally { reorderingTabs=false; }
        }
        foreach(var item in Tabs.Items.Cast<TabItem>())
        {
            var transform=TabTranslation(item); transform.BeginAnimation(TranslateTransform.XProperty,null);
            transform.X=before.TryGetValue(item,out double x) ? x-TabLayoutLeft(item) : 0;
            AnimateTab(item,0);
        }
        foreach(var old in tabTranslations.Keys.Where(t=>!Tabs.Items.Contains(t)).ToList()) tabTranslations.Remove(old);
        tabDragOrder=[]; tabDragLeft.Clear(); tabDragPanel=null; tabDropIndex=-1;
    }
}
