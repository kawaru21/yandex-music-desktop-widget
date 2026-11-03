using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Path = System.IO.Path;

namespace MusicWidget {
static class Program {
    internal static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YandexMusicWidget");
    static Mutex single;
    internal static bool VerifyUi;
    internal static bool Inspect;
    internal static bool TrayStart;
    internal static EventWaitHandle ShowRequest;
    internal static long LastPulse;
    [STAThread] static void Main(string[] args) {
        Directory.CreateDirectory(Data);
        VerifyUi=args.Contains("--verify-ui");
        Inspect=args.Contains("--inspect");
        TrayStart=args.Contains("--tray");
        if(args.Contains("--recover"))Thread.Sleep(3000);
        if (args.Contains("--self-test")) { Tests.Run(); return; }
        if (args.Contains("--probe-volume")) { var v=AudioVolume.Get();File.WriteAllText(Path.Combine(Data,"volume-probe.txt"),"Yandex session volume="+(v.HasValue?v.Value.ToString("F3")+" set-current="+AudioVolume.Set(v.Value):"unavailable")); return; }
        if (args.Contains("--probe-media")) {var manager=Media.Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync()).GetAwaiter().GetResult();var s=manager.GetSessions().FirstOrDefault(x=>Media.IsYandex(x.SourceAppUserModelId));if(s!=null){var p=s.GetPlaybackInfo();File.WriteAllText(Path.Combine(Data,"media-probe.txt"),"Repeat="+p.AutoRepeatMode+" Shuffle="+p.IsShuffleActive);}return;}
        if(!VerifyUi&&!Inspect&&!args.Contains("--recover"))try{if(InstallPortable())return;}catch(Exception e){Log(e);MessageBox.Show("Не удалось установить виджет: "+e.Message,"Яндекс Музыка — виджет",MessageBoxButton.OK,MessageBoxImage.Error);return;}
        bool created; single = new Mutex(true, "Local\\YandexMusicPaletteWidget", out created);
        if (!created) {
            if(TrayStart||VerifyUi){single.Dispose();return;}
            // A manual launch must work even if the old UI thread cannot receive the show signal.
            var currentId=Process.GetCurrentProcess().Id;
            foreach(var old in Process.GetProcessesByName("MusicWidget"))try{if(old.Id!=currentId){old.Kill();old.WaitForExit(3000);}}catch(Exception e){Log(e);}
            single.Dispose();
            for(int attempt=0;attempt<20;attempt++){Thread.Sleep(150);single=new Mutex(true,"Local\\YandexMusicPaletteWidget",out created);if(created)break;single.Dispose();}
            if(!created)return;
        }
        ShowRequest=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\YandexMusicPaletteWidget.Show");
        Interlocked.Exchange(ref LastPulse,DateTime.UtcNow.Ticks);
        Task.Run(()=>{while(true){Thread.Sleep(5000);try{var statusPath=Path.Combine(Data,"status.json");bool pulseFresh=DateTime.UtcNow.Ticks-Interlocked.Read(ref LastPulse)<TimeSpan.FromSeconds(60).Ticks;bool statusFresh=File.Exists(statusPath)&&DateTime.UtcNow-File.GetLastWriteTimeUtc(statusPath)<TimeSpan.FromSeconds(60);if(pulseFresh&&statusFresh)continue;var path=Process.GetCurrentProcess().MainModule.FileName;Process.Start(new ProcessStartInfo(path,"--recover"){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(path)});Process.GetCurrentProcess().Kill();}catch(Exception e){Log(e);}}});
        var app = new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.DispatcherUnhandledException += (s,e) => { Log(e.Exception); e.Handled = true; };
        try { app.Run(new Widget()); } catch (Exception e) { Log(e);MessageBox.Show("Не удалось открыть виджет: "+e.Message,"Яндекс Музыка — виджет",MessageBoxButton.OK,MessageBoxImage.Error); }
        ShowRequest.Dispose();single.ReleaseMutex(); single.Dispose();
    }
    static bool InstallPortable(){
        var current=Process.GetCurrentProcess().MainModule.FileName;
        var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","YandexMusicWidget");
        var installed=Path.Combine(directory,"MusicWidget.exe");
        if(!string.Equals(current,installed,StringComparison.OrdinalIgnoreCase)){
            foreach(var old in Process.GetProcessesByName("MusicWidget"))try{if(old.Id!=Process.GetCurrentProcess().Id){old.Kill();old.WaitForExit(3000);}}catch(Exception e){Log(e);}
            Directory.CreateDirectory(directory);
            File.Copy(current,installed,true);
        }
        Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run","YandexMusicWidget","\""+installed+"\"");
        var obsolete=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),"Яндекс Музыка — виджет.lnk");
        if(File.Exists(obsolete))File.Delete(obsolete);
        if(string.Equals(current,installed,StringComparison.OrdinalIgnoreCase))return false;
        Process.Start(new ProcessStartInfo(installed){UseShellExecute=false,WorkingDirectory=directory});return true;
    }
    internal static void Log(Exception e) {
        try { var p=Path.Combine(Data,"errors.log"); if(File.Exists(p)&&new FileInfo(p).Length>262144) File.WriteAllText(p,""); File.AppendAllText(p,DateTime.Now.ToString("s")+" "+e+Environment.NewLine); } catch {}
    }
}
static class Media {
    // Native Windows metadata allows building with the installed .NET Framework.
    internal static async Task<T> Wait<T>(IAsyncOperation<T> op) {
        var deadline=DateTime.UtcNow.AddSeconds(8);
        try {
            while(op.Status==AsyncStatus.Started) { if(DateTime.UtcNow>deadline) {op.Cancel(); throw new TimeoutException("Windows media session timeout");} await Task.Delay(30); }
            return op.GetResults();
        } finally { op.Close(); }
    }
    internal static bool IsYandex(string id) {
        return !string.IsNullOrEmpty(id) && (id.IndexOf("yandex",StringComparison.OrdinalIgnoreCase)>=0 || id.IndexOf("яндекс",StringComparison.OrdinalIgnoreCase)>=0) && id.IndexOf("music",StringComparison.OrdinalIgnoreCase)>=0;
    }
    internal static double Position(double position,double start,double end,DateTimeOffset updated,bool playing,double rate) {
        var elapsed=playing?Math.Max(0,(DateTimeOffset.UtcNow-updated).TotalSeconds)*rate:0;
        return Math.Max(0,Math.Min(Math.Max(0,end-start),position-start+elapsed));
    }
    internal static string Time(double s) {
        s=Math.Max(0,s); var t=TimeSpan.FromSeconds(Math.Floor(s));
        return s>=3600?((int)t.TotalHours)+":"+t.Minutes.ToString("00")+":"+t.Seconds.ToString("00"):((int)t.TotalMinutes)+":"+t.Seconds.ToString("00");
    }
}
class Settings {
    public double X {get;set;}
    public double Y {get;set;}
    public bool HasPosition {get;set;}
    public bool OnTop {get;set;}
    public bool? AnimateBackground {get;set;}
    public double MotionSpeed {get;set;}
    public double WidgetScale {get;set;}
}
static class Palette {
    internal static Color Pick(BitmapSource bitmap) {return Extract(bitmap)[0];}
    internal static Color[] Extract(BitmapSource bitmap) {
        var small=new TransformedBitmap(bitmap,new ScaleTransform(48.0/bitmap.PixelWidth,48.0/bitmap.PixelHeight));
        var b=new FormatConvertedBitmap(small,PixelFormats.Bgra32,null,0);
        var bytes=new byte[b.PixelWidth*b.PixelHeight*4]; b.CopyPixels(bytes,b.PixelWidth*4,0);
        var bins=new Dictionary<int,double[]>();
        for(int i=0;i<bytes.Length;i+=4) {
            double r=bytes[i+2],g=bytes[i+1],bl=bytes[i]; var max=Math.Max(r,Math.Max(g,bl)); var min=Math.Min(r,Math.Min(g,bl));
            if(bytes[i+3]<180) continue;
            int key=((int)r/32<<6)|((int)g/32<<3)|((int)bl/32);
            double[] v; if(!bins.TryGetValue(key,out v)) {v=new double[4];bins[key]=v;}
            double w=0.16+(max-min)/255.0; v[0]+=w;v[1]+=r*w;v[2]+=g*w;v[3]+=bl*w;
        }
        if(bins.Count==0) return new[]{Color.FromRgb(145,150,166)};
        var candidates=bins.Values.OrderByDescending(v=>v[0]).ToArray();
        var selected=new List<Color>();
        while(selected.Count<8&&candidates.Length>0) {
            var best=candidates.OrderByDescending(v=>v[0]*(selected.Count==0?1:Math.Sqrt(selected.Min(c=>Distance(c,Color.FromRgb((byte)(v[1]/v[0]),(byte)(v[2]/v[0]),(byte)(v[3]/v[0]))))))).First();
            var color=Color.FromRgb((byte)(best[1]/best[0]),(byte)(best[2]/best[0]),(byte)(best[3]/best[0]));
            selected.Add(color);
            candidates=candidates.Where(v=>Distance(color,Color.FromRgb((byte)(v[1]/v[0]),(byte)(v[2]/v[0]),(byte)(v[3]/v[0])))>1100).ToArray();
        }
        return selected.ToArray();
    }
    static double Distance(Color a,Color b){return (a.R-b.R)*(a.R-b.R)+(a.G-b.G)*(a.G-b.G)+(a.B-b.B)*(a.B-b.B);}
    internal static Color Mix(Color a,Color b,double n) { return Color.FromRgb((byte)(a.R*(1-n)+b.R*n),(byte)(a.G*(1-n)+b.G*n),(byte)(a.B*(1-n)+b.B*n)); }
}
// Blurred artwork preserves the complete image palette; drifting translucent
// color fields add independent, continuous motion without moving the text.
class LivingBackground : FrameworkElement {
    readonly Stopwatch time=Stopwatch.StartNew();
    readonly DispatcherTimer frames;
    readonly RadialGradientBrush[] fields=new RadialGradientBrush[8];
    Color[] from=new Color[8],to=new Color[8];
    BitmapSource cover,oldCover;
    double changedAt;
    bool animate=true;
    internal int FrameCount {get;private set;}
    internal Color[] ColorsUsed {get;private set;}
    internal bool Animate {get{return animate;}set{animate=value;UpdateClock();InvalidateVisual();}}
    internal double Speed {get;set;}
    internal LivingBackground() {
        Speed=1;
        IsHitTestVisible=false;Effect=new System.Windows.Media.Effects.BlurEffect{Radius=26,RenderingBias=System.Windows.Media.Effects.RenderingBias.Performance};
        for(int i=0;i<8;i++){from[i]=to[i]=Color.FromRgb(66,71,100);fields[i]=new RadialGradientBrush();fields[i].GradientStops.Add(new GradientStop(Colors.Transparent,0));fields[i].GradientStops.Add(new GradientStop(Colors.Transparent,1));}
        ColorsUsed=new[]{to[0]};frames=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(40)};frames.Tick+=(s,e)=>{InvalidateVisual();if(!animate&&time.Elapsed.TotalSeconds-changedAt>1.8)frames.Stop();};
        IsVisibleChanged+=(s,e)=>UpdateClock();Unloaded+=(s,e)=>frames.Stop();
    }
    void UpdateClock(){if(IsVisible&&(animate||time.Elapsed.TotalSeconds-changedAt<1.8))frames.Start();else frames.Stop();}
    internal void SetCover(BitmapSource bitmap) {
        double mix=Math.Min(1,(time.Elapsed.TotalSeconds-changedAt)/1.8);
        for(int i=0;i<8;i++)from[i]=Palette.Mix(from[i],to[i],mix);
        ColorsUsed=bitmap==null?new[]{Color.FromRgb(70,80,120),Color.FromRgb(44,62,83)}:Palette.Extract(bitmap);
        for(int i=0;i<8;i++)to[i]=ColorsUsed[i%ColorsUsed.Length];
        oldCover=cover;cover=bitmap;changedAt=time.Elapsed.TotalSeconds;UpdateClock();InvalidateVisual();
    }
    internal static Point FieldPosition(int i,double t,double w,double h) {
        return new Point(w*(.5+.46*Math.Sin(t*.16+i*2.4)),h*(.5+.46*Math.Cos(t*.13+i*1.7)));
    }
    protected override void OnRender(DrawingContext dc) {
        double w=ActualWidth,h=ActualHeight;if(w<=0||h<=0)return;FrameCount++;
        double t=animate?time.Elapsed.TotalSeconds*Speed:0;
        double fade=Math.Min(1,(time.Elapsed.TotalSeconds-changedAt)/1.8);fade=fade*fade*(3-2*fade);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(14,17,24)),null,new Rect(-30,-30,w+60,h+60));
        // Overscan keeps the rounded perimeter filled throughout the movement.
        var rect=new Rect(-w*.22+Math.Sin(t*.10)*w*.13,-h*.35+Math.Cos(t*.12)*h*.13,w*1.5,h*1.7);
        if(oldCover!=null&&fade<1){dc.PushOpacity(.72*(1-fade));dc.DrawImage(oldCover,rect);dc.Pop();}
        if(cover!=null){dc.PushOpacity(.72*fade);dc.DrawImage(cover,rect);dc.Pop();}
        for(int i=0;i<8;i++) {
            var c=Palette.Mix(from[i],to[i],fade);fields[i].GradientStops[0].Color=Color.FromArgb(190,c.R,c.G,c.B);fields[i].GradientStops[1].Color=Color.FromArgb(0,c.R,c.G,c.B);
            var center=FieldPosition(i,t,w,h);double pulse=1+.14*Math.Sin(t*.21+i);
            dc.DrawEllipse(fields[i],null,center,w*.44*pulse,h*.88*pulse);
        }
    }
}
class Widget : Window {
    LivingBackground living;
    Grid root;
    Slider volume;
    bool volumeBusy,volumeDrag;
    bool volumeWriteActive,volumePollActive;
    DateTime lastVolumePoll=DateTime.MinValue;
    double requestedVolume;
    DispatcherTimer volumeTimer;
    Border card,art,progressTrack,progressFill;
    TextBlock title,artist,status,elapsed,total,placeholder;
    Button play,previous,next;
    SolidColorBrush accent=new SolidColorBrush(Color.FromRgb(194,199,230));
    GradientStop backgroundA=new GradientStop(Color.FromRgb(39,42,57),0),backgroundB=new GradientStop(Color.FromRgb(19,21,30),1);
    GlobalSystemMediaTransportControlsSessionManager manager;
    GlobalSystemMediaTransportControlsSession session;
    GlobalSystemMediaTransportControlsSessionTimelineProperties timeline;
    bool busy,playing,closed,dragging,initialized,metadataDirty=true,exitRequested;
    double rate=1,dragX,dragY;
    string trackKey="";
    DateTime nextMetadata=DateTime.MinValue,lastDiagnostic=DateTime.MinValue,lastError=DateTime.MinValue;
    Settings settings;
    IntPtr handle,parent;
    DispatcherTimer poll,clock;
    System.Windows.Forms.NotifyIcon tray;
    readonly JavaScriptSerializer json=new JavaScriptSerializer();
    readonly string config=Path.Combine(Program.Data,"settings.json");

    internal Widget() {
        Title="Яндекс Музыка — виджет";Width=400;Height=224;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        AllowsTransparency=true;Background=Brushes.Transparent;ShowInTaskbar=Program.Inspect;ShowActivated=!Program.TrayStart;FontFamily=new FontFamily("Segoe UI");
        settings=LoadSettings();Width=400*ScaleValue();Height=224*ScaleValue(); Left=SystemParameters.WorkArea.Right-Width-28;Top=SystemParameters.WorkArea.Bottom-Height-28;
        Build();
        Application.Current.SessionEnding+=(s,e)=>exitRequested=true;
        SourceInitialized+=(s,e)=> {handle=new WindowInteropHelper(this).Handle;EnforceTrayWindow();};
        Loaded+=async(s,e)=> { if(initialized)return;initialized=true;ApplyLayer(); SetupTray();if(Program.TrayStart)HideWidget();else Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(ShowWidget)); poll=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(650)};poll.Tick+=async(a,b)=>await Refresh();poll.Start();clock=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};clock.Tick+=(a,b)=>{Interlocked.Exchange(ref Program.LastPulse,DateTime.UtcNow.Ticks);if(Program.ShowRequest.WaitOne(0))ShowWidget();Tick();};clock.Start(); await Refresh();if(Program.VerifyUi)await VerifyUi(); };
        Closing+=(s,e)=>{if(!exitRequested){e.Cancel=true;HideWidget();}};
        Closed+=(s,e)=> {closed=true;if(poll!=null)poll.Stop();if(clock!=null)clock.Stop();Unsubscribe();SaveSettings();if(tray!=null){var icon=tray.Icon;tray.Dispose();if(icon!=null)icon.Dispose();}};
        MouseLeftButtonDown+=(s,e)=> {Native.POINT p;Native.GetCursorPos(out p);Native.RECT r;Native.GetWindowRect(handle,out r);dragX=p.X-r.Left;dragY=p.Y-r.Top;dragging=true;CaptureMouse();};
        MouseMove+=(s,e)=> {if(!dragging)return;Native.POINT p;Native.GetCursorPos(out p);Place(p.X-dragX,p.Y-dragY);};
        MouseLeftButtonUp+=(s,e)=>{if(!dragging)return;dragging=false;ReleaseMouseCapture();KeepOnScreen();SaveSettings();};
        LostMouseCapture+=(s,e)=>{dragging=false;};
    }
    TextBlock Text(string value,double size,Brush brush) {return new TextBlock{Text=value,FontSize=size,Foreground=brush,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};}
    Button Button(string glyph,string hint,double width=30) {
        var b=new Button{Content=glyph,ToolTip=hint,Width=width,Height=28,FontSize=15,Foreground=Brushes.White,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Cursor=Cursors.Hand,Padding=new Thickness(0),Focusable=false};
        System.Windows.Automation.AutomationProperties.SetName(b,hint);
        var f=new FrameworkElementFactory(typeof(Border)); f.SetValue(Border.CornerRadiusProperty,new CornerRadius(13));f.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
        var c=new FrameworkElementFactory(typeof(ContentPresenter));c.SetValue(ContentPresenter.HorizontalAlignmentProperty,HorizontalAlignment.Center);c.SetValue(ContentPresenter.VerticalAlignmentProperty,VerticalAlignment.Center);f.AppendChild(c);
        b.Template=new ControlTemplate(typeof(Button)){VisualTree=f};
        b.MouseEnter+=(s,e)=>b.BeginAnimation(OpacityProperty,new DoubleAnimation(.68,TimeSpan.FromMilliseconds(170)));
        b.MouseLeave+=(s,e)=>b.BeginAnimation(OpacityProperty,new DoubleAnimation(1,TimeSpan.FromMilliseconds(220)));
        return b;
    }
    void Build() {
        root=new Grid{Width=400,Height=224};root.Clip=new RectangleGeometry(new Rect(0,0,400,224),26,26);
        Content=new Viewbox{Stretch=Stretch.Fill,Child=root};
        living=new LivingBackground{Animate=settings.AnimateBackground??true,Speed=SpeedValue()};root.Children.Add(living);
        card=new Border{CornerRadius=new CornerRadius(26),BorderBrush=new SolidColorBrush(Color.FromArgb(48,255,255,255)),BorderThickness=new Thickness(1),Padding=new Thickness(22,15,22,16),Background=new LinearGradientBrush(Color.FromArgb(60,8,10,17),Color.FromArgb(150,8,10,17),90)};root.Children.Add(card);
        var layout=new Grid();card.Child=layout;layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(27)});layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(96)});layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(19)});layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(38)});
        var header=new DockPanel();layout.Children.Add(header);
        var menu=Button("···","Настройки виджета",28);menu.FontSize=22;DockPanel.SetDock(menu,Dock.Right);header.Children.Add(menu);menu.Click+=(s,e)=>OpenMenu();
        var brand=Text("♫  ЯНДЕКС МУЗЫКА",10.5,new SolidColorBrush(Color.FromArgb(190,255,255,255)));brand.FontWeight=FontWeights.SemiBold;header.Children.Add(brand);
        var body=new Grid{Margin=new Thickness(0,7,0,0)};Grid.SetRow(body,1);layout.Children.Add(body);body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(88)});body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(18)});body.ColumnDefinitions.Add(new ColumnDefinition());
        art=new Border{Width=86,Height=86,CornerRadius=new CornerRadius(17),Background=new SolidColorBrush(Color.FromArgb(20,255,255,255)),VerticalAlignment=VerticalAlignment.Top};placeholder=Text("♫",37,new SolidColorBrush(Color.FromArgb(170,255,255,255)));placeholder.HorizontalAlignment=HorizontalAlignment.Center;art.Child=placeholder;body.Children.Add(art);
        var info=new Grid();Grid.SetColumn(info,2);body.Children.Add(info);info.RowDefinitions.Add(new RowDefinition{Height=new GridLength(47)});info.RowDefinitions.Add(new RowDefinition{Height=new GridLength(22)});info.RowDefinitions.Add(new RowDefinition{Height=new GridLength(18)});
        title=Text("Включите музыку",19,Brushes.White);title.FontWeight=FontWeights.SemiBold;title.TextWrapping=TextWrapping.Wrap;title.LineHeight=23;title.MaxHeight=46;info.Children.Add(title);
        artist=Text("в приложении Яндекс Музыка",12,new SolidColorBrush(Color.FromArgb(190,255,255,255)));Grid.SetRow(artist,1);info.Children.Add(artist);
        status=Text("Ожидание трека",10,accent);Grid.SetRow(status,2);info.Children.Add(status);
        progressTrack=new Border{Height=4,CornerRadius=new CornerRadius(2),Background=new SolidColorBrush(Color.FromArgb(28,255,255,255)),VerticalAlignment=VerticalAlignment.Bottom,Cursor=Cursors.Hand};Grid.SetRow(progressTrack,2);layout.Children.Add(progressTrack);
        progressFill=new Border{Height=4,Width=0,HorizontalAlignment=HorizontalAlignment.Left,CornerRadius=new CornerRadius(2),Background=accent,IsHitTestVisible=false};progressTrack.Child=progressFill;
        progressTrack.MouseLeftButtonDown+=async(s,e)=>{e.Handled=true; if(session!=null&&timeline!=null&&session.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled) {double fraction=Math.Max(0,Math.Min(1,e.GetPosition(progressTrack).X/progressTrack.ActualWidth));await Command("seek",(long)(timeline.StartTime.Ticks+fraction*(timeline.EndTime-timeline.StartTime).Ticks));}};
        var footer=new Grid();Grid.SetRow(footer,3);layout.Children.Add(footer);
        elapsed=Text("0:00",11,new SolidColorBrush(Color.FromArgb(215,255,255,255)));elapsed.HorizontalAlignment=HorizontalAlignment.Left;footer.Children.Add(elapsed);
        total=Text("—:—",11,new SolidColorBrush(Color.FromArgb(155,255,255,255)));total.HorizontalAlignment=HorizontalAlignment.Right;footer.Children.Add(total);
        var controls=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};footer.Children.Add(controls);
        previous=Button("\uE892","Предыдущий трек",34);previous.FontFamily=new FontFamily("Segoe MDL2 Assets");previous.Click+=async(s,e)=>await Command("previous");controls.Children.Add(previous);
        play=Button("\uE768","Воспроизведение / пауза",40);play.FontFamily=new FontFamily("Segoe MDL2 Assets");play.Background=new SolidColorBrush(Color.FromArgb(24,255,255,255));play.Click+=async(s,e)=>await Command("toggle");controls.Children.Add(play);
        next=Button("\uE893","Следующий трек",34);next.FontFamily=new FontFamily("Segoe MDL2 Assets");next.Click+=async(s,e)=>await Command("next");controls.Children.Add(next);
        volume=new Slider{Minimum=0,Maximum=100,Width=94,Height=20,VerticalAlignment=VerticalAlignment.Center,ToolTip="Громкость Яндекс Музыки",Margin=new Thickness(12,0,0,0),IsMoveToPointEnabled=true};
        volume.PreviewMouseLeftButtonDown+=(s,e)=>{volumeDrag=true;volume.CaptureMouse();volume.Value=ClickVolume(e.GetPosition(volume).X,volume.ActualWidth);e.Handled=true;};
        volume.PreviewMouseMove+=(s,e)=>{if(volumeDrag&&e.LeftButton==MouseButtonState.Pressed){volume.Value=ClickVolume(e.GetPosition(volume).X,volume.ActualWidth);e.Handled=true;}};
        volume.PreviewMouseLeftButtonUp+=(s,e)=>{if(!volumeDrag)return;volume.Value=ClickVolume(e.GetPosition(volume).X,volume.ActualWidth);volumeDrag=false;volume.ReleaseMouseCapture();e.Handled=true;};
        volume.LostMouseCapture+=(s,e)=>volumeDrag=false;
        volumeTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(45)};volumeTimer.Tick+=async(s,e)=>{if(volumeWriteActive)return;volumeTimer.Stop();volumeWriteActive=true;var value=requestedVolume;try{await Task.Run(()=>AudioVolume.Set(value/100));}finally{volumeWriteActive=false;if(Math.Abs(requestedVolume-value)>.01)volumeTimer.Start();}};
        volume.ValueChanged+=(s,e)=>{if(!volumeBusy&&(volumeDrag||volume.IsKeyboardFocusWithin))QueueVolume(volume.Value);};controls.Children.Add(volume);
        ContextMenu=new ContextMenu();ContextMenuOpening+=(s,e)=>{e.Handled=true;OpenMenu();};
    }
    void OpenMenu() {
        var menu=new ContextMenu{Background=new SolidColorBrush(Color.FromRgb(30,32,44)),Foreground=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromRgb(104,91,153)),BorderThickness=new Thickness(1),Padding=new Thickness(8),FontFamily=new FontFamily("Segoe UI"),FontSize=13};
        var shell=new FrameworkElementFactory(typeof(Border));shell.SetValue(Border.CornerRadiusProperty,new CornerRadius(14));shell.SetValue(Border.BackgroundProperty,menu.Background);shell.SetValue(Border.BorderBrushProperty,menu.BorderBrush);shell.SetValue(Border.BorderThicknessProperty,new Thickness(1));shell.SetValue(Border.PaddingProperty,new Thickness(8));shell.SetValue(Border.EffectProperty,new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=18,ShadowDepth=5,Opacity=.4,Color=Colors.Black});var items=new FrameworkElementFactory(typeof(ItemsPresenter));shell.AppendChild(items);menu.Template=new ControlTemplate(typeof(ContextMenu)){VisualTree=shell};
        AddMenu(menu,"Скрыть в трей",()=>HideWidget());
        AddMenu(menu,"Открыть Яндекс Музыку",()=>OpenMusic());
        var top=new MenuItem{Header="Поверх всех окон",IsCheckable=true,IsChecked=settings.OnTop};top.Click+=(s,e)=>{SavePosition();settings.OnTop=top.IsChecked;ApplyLayer();SaveSettings();};menu.Items.Add(top);
        var motion=new MenuItem{Header="Живой фон",IsCheckable=true,IsChecked=living.Animate};motion.Click+=(s,e)=>SetMotion(motion.IsChecked);menu.Items.Add(motion);
        var speed=new MenuItem{Header="Скорость цвета"};menu.Items.Add(speed);foreach(var option in new[]{Tuple.Create("Медленно",.5),Tuple.Create("Обычно",1.0),Tuple.Create("Быстро",2.0),Tuple.Create("Очень быстро",3.0)}){var item=new MenuItem{Header=option.Item1,IsCheckable=true,IsChecked=Math.Abs(SpeedValue()-option.Item2)<.01};item.Click+=(s,e)=>SetSpeed(option.Item2);speed.Items.Add(item);}
        var size=new MenuItem{Header="Размер виджета"};menu.Items.Add(size);foreach(var option in new[]{Tuple.Create("Компактный",.8),Tuple.Create("Обычный",1.0),Tuple.Create("Крупный",1.25),Tuple.Create("Очень крупный",1.5)}){var item=new MenuItem{Header=option.Item1,IsCheckable=true,IsChecked=Math.Abs(ScaleValue()-option.Item2)<.01};item.Click+=(s,e)=>SetScale(option.Item2);size.Items.Add(item);}
        AddMenu(menu,"Вернуть в правый нижний угол",()=>{settings.HasPosition=false;ApplyLayer();SaveSettings();});
        menu.PlacementTarget=this;menu.IsOpen=true;
    }
    void AddMenu(ContextMenu menu,string name,Action action) {var item=new MenuItem{Header=name};item.Click+=(s,e)=>action();menu.Items.Add(item);}
    void OpenMusic() {try {var exe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","YandexMusic","Яндекс Музыка.exe");Process.Start(File.Exists(exe)?exe:"https://music.yandex.ru");}catch(Exception e){Program.Log(e);}}
    void SetupTray() {
        tray=new System.Windows.Forms.NotifyIcon{Text="Яндекс Музыка — виджет",Icon=MakeTrayIcon(),Visible=true};
        var menu=new System.Windows.Forms.ContextMenuStrip{BackColor=System.Drawing.Color.FromArgb(30,32,44),ForeColor=System.Drawing.Color.White,Font=new System.Drawing.Font("Segoe UI",10),Padding=new System.Windows.Forms.Padding(8),ShowImageMargin=false};menu.Renderer=new System.Windows.Forms.ToolStripProfessionalRenderer(new MenuColors());
        var show=menu.Items.Add("Показать виджет",null,(s,e)=>Dispatcher.BeginInvoke(new Action(ShowWidget)));
        var hide=menu.Items.Add("Скрыть виджет",null,(s,e)=>Dispatcher.Invoke(new Action(HideWidget)));
        var prefs=new System.Windows.Forms.ToolStripMenuItem("Настройки виджета");menu.Items.Add(prefs);
        var top=new System.Windows.Forms.ToolStripMenuItem("Поверх всех окон"){CheckOnClick=true};prefs.DropDownItems.Add(top);
        top.Click+=(s,e)=>Dispatcher.Invoke(new Action(()=>{SavePosition();settings.OnTop=top.Checked;if(IsVisible)ApplyLayer();SaveSettings();}));
        var motion=new System.Windows.Forms.ToolStripMenuItem("Живой фон"){CheckOnClick=true};prefs.DropDownItems.Add(motion);motion.Click+=(s,e)=>Dispatcher.Invoke(new Action(()=>SetMotion(motion.Checked)));
        var speedMenu=new System.Windows.Forms.ToolStripMenuItem("Скорость цвета");prefs.DropDownItems.Add(speedMenu);foreach(var option in new[]{Tuple.Create("Медленно",.5),Tuple.Create("Обычно",1.0),Tuple.Create("Быстро",2.0),Tuple.Create("Очень быстро",3.0)}){var item=new System.Windows.Forms.ToolStripMenuItem(option.Item1){Tag=option.Item2};item.Click+=(s,e)=>SetSpeed((double)((System.Windows.Forms.ToolStripMenuItem)s).Tag);speedMenu.DropDownItems.Add(item);}
        var sizeMenu=new System.Windows.Forms.ToolStripMenuItem("Размер виджета");prefs.DropDownItems.Add(sizeMenu);foreach(var option in new[]{Tuple.Create("Компактный",.8),Tuple.Create("Обычный",1.0),Tuple.Create("Крупный",1.25),Tuple.Create("Очень крупный",1.5)}){var item=new System.Windows.Forms.ToolStripMenuItem(option.Item1){Tag=option.Item2};item.Click+=(s,e)=>SetScale((double)((System.Windows.Forms.ToolStripMenuItem)s).Tag);sizeMenu.DropDownItems.Add(item);}
        prefs.DropDownOpening+=(s,e)=>{foreach(System.Windows.Forms.ToolStripMenuItem item in speedMenu.DropDownItems)item.Checked=Math.Abs((double)item.Tag-SpeedValue())<.01;foreach(System.Windows.Forms.ToolStripMenuItem item in sizeMenu.DropDownItems)item.Checked=Math.Abs((double)item.Tag-ScaleValue())<.01;};
        prefs.DropDownItems.Add("Вернуть в правый нижний угол",null,(s,e)=>Dispatcher.Invoke(new Action(()=>{settings.HasPosition=false;ShowWidget();SaveSettings();})));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());menu.Items.Add("Открыть Яндекс Музыку",null,(s,e)=>Dispatcher.Invoke(new Action(OpenMusic)));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());menu.Items.Add("Закрыть окно",null,(s,e)=>Dispatcher.BeginInvoke(new Action(HideWidget)));
        menu.Opening+=(s,e)=>{show.Enabled=true;hide.Enabled=IsVisible;top.Checked=settings.OnTop;motion.Checked=living.Animate;};
        tray.ContextMenuStrip=menu;
        tray.MouseClick+=(s,e)=>{if(e.Button==System.Windows.Forms.MouseButtons.Left)Dispatcher.BeginInvoke(new Action(ShowWidget));};
    }
    System.Drawing.Icon MakeTrayIcon() {
        using(var bitmap=new System.Drawing.Bitmap(32,32))using(var g=System.Drawing.Graphics.FromImage(bitmap)) {
            g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using(var brush=new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.Rectangle(0,0,32,32),System.Drawing.Color.FromArgb(111,85,236),System.Drawing.Color.FromArgb(239,156,88),45))g.FillEllipse(brush,1,1,30,30);
            using(var pen=new System.Drawing.Pen(System.Drawing.Color.White,2.5f)){g.DrawLine(pen,14,10,14,23);g.DrawLine(pen,14,10,24,8);g.DrawLine(pen,24,8,24,21);}
            g.FillEllipse(System.Drawing.Brushes.White,8,20,7,5);g.FillEllipse(System.Drawing.Brushes.White,18,18,7,5);
            var h=bitmap.GetHicon();try{using(var icon=System.Drawing.Icon.FromHandle(h))return (System.Drawing.Icon)icon.Clone();}finally{Native.DestroyIcon(h);}
        }
    }
    void SetMotion(bool value){settings.AnimateBackground=value;living.Animate=value;SaveSettings();}
    double SpeedValue(){return settings.MotionSpeed<=0?1:settings.MotionSpeed;}
    double ScaleValue(){return settings.WidgetScale<=0?1:settings.WidgetScale;}
    void SetSpeed(double speed){settings.MotionSpeed=speed;living.Speed=speed;SaveSettings();}
    void QueueVolume(double value){requestedVolume=value;volumeTimer.Start();}
    async void PollVolume(){volumePollActive=true;try{var v=await Task.Run(()=>AudioVolume.Get());if(!closed&&v.HasValue&&!volume.IsMouseCaptureWithin){volumeBusy=true;volume.Value=v.Value*100;volumeBusy=false;}}catch(Exception e){Program.Log(e);}finally{volumeBusy=false;volumePollActive=false;}}
    internal static double ClickVolume(double x,double width){return width<=0?0:Math.Max(0,Math.Min(100,x/width*100));}
    void SetScale(double scale){SavePosition();settings.WidgetScale=scale;double oldWidth=Width,oldHeight=Height,targetWidth=400*scale,targetHeight=224*scale;BeginAnimation(WidthProperty,null);BeginAnimation(HeightProperty,null);Width=targetWidth;Height=targetHeight;var easing=new CubicEase{EasingMode=EasingMode.EaseInOut};var duration=TimeSpan.FromMilliseconds(280);BeginAnimation(WidthProperty,new DoubleAnimation(oldWidth,targetWidth,duration){EasingFunction=easing});BeginAnimation(HeightProperty,new DoubleAnimation(oldHeight,targetHeight,duration){EasingFunction=easing});Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{KeepOnScreen();SaveSettings();}));}
    void HideWidget(){SaveSettings();Hide();}
    void Quit(){exitRequested=true;Application.Current.Shutdown();}
    void ShowWidget(){if(closed)return;ShowActivated=true;Show();ApplyLayer();EnforceTrayWindow();Activate();}
    void EnforceTrayWindow(){if(Program.Inspect)return;ShowInTaskbar=false;if(handle!=IntPtr.Zero)Native.SetWindowLong(handle,-20,(Native.GetWindowLong(handle,-20)|0x80)&~0x40000);}
    void Unsubscribe() {if(session==null)return;try{session.MediaPropertiesChanged-=OnMediaChanged;}catch{}}
    void OnMediaChanged(GlobalSystemMediaTransportControlsSession s,MediaPropertiesChangedEventArgs e) {Dispatcher.BeginInvoke(new Action(()=>metadataDirty=true));}
    async Task Refresh() {
        if(busy||closed)return;busy=true;
        try {
            if(manager==null)manager=await Media.Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
            var candidate=manager.GetSessions().Where(s=>Media.IsYandex(s.SourceAppUserModelId)).OrderByDescending(s=>s.GetPlaybackInfo().PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing).FirstOrDefault();
            if(!object.Equals(candidate,session)) {Unsubscribe();session=candidate;trackKey="";metadataDirty=true;timeline=null;if(session!=null)session.MediaPropertiesChanged+=OnMediaChanged;}
            if(session==null) {Empty();return;}
            var p=session.GetPlaybackInfo();playing=p.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;rate=p.PlaybackRate??1;
            timeline=session.GetTimelineProperties();
            play.IsEnabled=p.Controls.IsPlayPauseToggleEnabled;previous.IsEnabled=p.Controls.IsPreviousEnabled;next.IsEnabled=p.Controls.IsNextEnabled;
            play.Content=playing?"\uE769":"\uE768";status.Text=playing?"Сейчас играет":"На паузе";
            if(metadataDirty||DateTime.UtcNow>=nextMetadata) {
                var current=session;var m=await Media.Wait(current.TryGetMediaPropertiesAsync());
                if(closed||!object.Equals(current,session))return;
                var key=m.Title+"\n"+m.Artist+"\n"+m.AlbumTitle;
                if(key!=trackKey||metadataDirty) {
                    title.Text=string.IsNullOrWhiteSpace(m.Title)?"Неизвестный трек":m.Title;title.ToolTip=m.Title;artist.Text=m.Artist;artist.ToolTip=m.Artist;
                    if(key!=trackKey){art.Background=new SolidColorBrush(Color.FromArgb(20,255,255,255));art.Child=placeholder;SetPalette(Color.FromRgb(130,140,170));}
                    if(m.Thumbnail!=null) {
                        using(var stream=await Media.Wait(m.Thumbnail.OpenReadAsync())) {
                            if(stream.Size>0&&stream.Size<16000000) using(var reader=new DataReader(stream)) {
                                await Media.Wait(reader.LoadAsync((uint)stream.Size));var bytes=new byte[(int)stream.Size];reader.ReadBytes(bytes);
                                var bmp=new BitmapImage();using(var mem=new MemoryStream(bytes)){bmp.BeginInit();bmp.CacheOption=BitmapCacheOption.OnLoad;bmp.DecodePixelWidth=192;bmp.StreamSource=mem;bmp.EndInit();bmp.Freeze();}
                                art.Child=null;art.Background=new ImageBrush(bmp){Stretch=Stretch.UniformToFill};SetPalette(Palette.Pick(bmp));living.SetCover(bmp);
                            }
                        }
                    }
                    if(m.Thumbnail==null)living.SetCover(null);
                    trackKey=key;
                    foreach(UIElement element in new UIElement[]{art,title,artist}){element.Opacity=0;element.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(300)));}
                }
                metadataDirty=false;nextMetadata=DateTime.UtcNow.AddSeconds(5);
            }
            Tick();
        } catch(Exception e) {if(DateTime.UtcNow-lastError>TimeSpan.FromMinutes(1)){Program.Log(e);lastError=DateTime.UtcNow;}manager=null;status.Text="Переподключение…";}
        finally{busy=false;}
    }
    void Empty() {playing=false;timeline=null;if(title.Text!="Включите музыку")living.SetCover(null);title.Text="Включите музыку";title.ToolTip=null;artist.Text="в приложении Яндекс Музыка";artist.ToolTip=null;status.Text="Ожидание трека";art.Child=placeholder;art.Background=new SolidColorBrush(Color.FromArgb(20,255,255,255));play.IsEnabled=previous.IsEnabled=next.IsEnabled=false;play.Content="\uE768";elapsed.Text="0:00";total.Text="—:—";progressFill.Width=0;}
    void SetPalette(Color color) {
        Animate(backgroundA,GradientStop.ColorProperty,Palette.Mix(color,Color.FromRgb(12,14,20),.68));
        Animate(backgroundB,GradientStop.ColorProperty,Palette.Mix(color,Color.FromRgb(10,12,17),.88));
        Animate(accent,SolidColorBrush.ColorProperty,Palette.Mix(color,Colors.White,.54));
    }
    void Animate(Animatable target,DependencyProperty property,Color value) {target.BeginAnimation(property,new ColorAnimation(value,TimeSpan.FromMilliseconds(650)));}
    void Tick() {
        double duration=0,position=0;
        if(timeline!=null){duration=Math.Max(0,(timeline.EndTime-timeline.StartTime).TotalSeconds);
            position=Media.Position(timeline.Position.TotalSeconds,timeline.StartTime.TotalSeconds,timeline.EndTime.TotalSeconds,timeline.LastUpdatedTime,playing,rate);
            elapsed.Text=Media.Time(position);total.Text=duration>0?Media.Time(Math.Ceiling(duration)):"—:—";progressFill.Width=duration>0?Math.Max(0,progressTrack.ActualWidth*position/duration):0;}
        if(!volumePollActive&&DateTime.UtcNow-lastVolumePoll>TimeSpan.FromSeconds(2)){lastVolumePoll=DateTime.UtcNow;PollVolume();}
        if(DateTime.UtcNow-lastDiagnostic>TimeSpan.FromSeconds(2)) {
            lastDiagnostic=DateTime.UtcNow;
            try{Native.RECT rect;Native.GetWindowRect(handle,out rect);File.WriteAllText(Path.Combine(Program.Data,"status.json"),json.Serialize(new{title=title.Text,artist=artist.Text,playing=playing,position=position,duration=duration,artwork=art.Child==null,palette=living.ColorsUsed.Select(c=>c.ToString()).ToArray(),animated=living.Animate,frames=living.FrameCount,accent=accent.Color.ToString(),tray=tray!=null&&tray.Visible,taskbar=ShowInTaskbar,extendedStyle=Native.GetWindowLong(handle,-20),desktop=!settings.OnTop,parent=parent.ToInt64(),visible=Native.IsWindowVisible(handle),x=rect.Left,y=rect.Top,width=rect.Right-rect.Left,height=rect.Bottom-rect.Top,updated=DateTime.UtcNow.ToString("o")}));}catch{}
            if(IsVisible&&!settings.OnTop&&(parent==IntPtr.Zero||!Native.IsWindow(parent)))ApplyLayer();
        }
    }
    async Task Command(string command,long ticks=0) {
        if(session==null)return;
        try {IAsyncOperation<bool> op=command=="previous"?session.TrySkipPreviousAsync():command=="next"?session.TrySkipNextAsync():command=="seek"?session.TryChangePlaybackPositionAsync(ticks):session.TryTogglePlayPauseAsync();await Media.Wait(op);metadataDirty=true;await Refresh();}catch(Exception e){Program.Log(e);}
    }
    Settings LoadSettings() {try{return json.Deserialize<Settings>(File.ReadAllText(config))??new Settings();}catch{return new Settings();}}
    void SavePosition(){Native.RECT r;if(handle!=IntPtr.Zero&&Native.GetWindowRect(handle,out r)){settings.X=r.Left;settings.Y=r.Top;settings.HasPosition=true;}}
    void SaveSettings(){try{SavePosition();File.WriteAllText(config,json.Serialize(settings));}catch(Exception e){Program.Log(e);}}
    void ApplyLayer() {
        if(handle==IntPtr.Zero)return;
        if(settings.OnTop){Native.SetParent(handle,IntPtr.Zero);Native.SetWindowLong(handle,-16,(Native.GetWindowLong(handle,-16)&~0x40000000)|unchecked((int)0x80000000));parent=IntPtr.Zero;Topmost=true;}
        else {Topmost=false;parent=Native.Desktop();if(parent!=IntPtr.Zero){Native.SetWindowLong(handle,-16,(Native.GetWindowLong(handle,-16)&~unchecked((int)0x80000000))|0x40000000);Native.SetParent(handle,parent);}}
        Native.RECT rect;Native.GetWindowRect(handle,out rect);var area=System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
        Place(settings.HasPosition?settings.X:area.Right-(rect.Right-rect.Left)-28,settings.HasPosition?settings.Y:area.Bottom-(rect.Bottom-rect.Top)-28);KeepOnScreen();
        EnforceTrayWindow();
    }
    void Place(double x,double y) {var p=new Native.POINT{X=(int)x,Y=(int)y};if(parent!=IntPtr.Zero)Native.ScreenToClient(parent,ref p);Native.SetWindowPos(handle,settings.OnTop?new IntPtr(-1):IntPtr.Zero,p.X,p.Y,0,0,0x0011|0x0040);}
    void KeepOnScreen() {Native.RECT r;if(!Native.GetWindowRect(handle,out r))return;var area=System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top)).WorkingArea;Place(Math.Max(area.Left,Math.Min(r.Left,area.Right-(r.Right-r.Left))),Math.Max(area.Top,Math.Min(r.Top,area.Bottom-(r.Bottom-r.Top))));}
    byte[] Snapshot(string name) {
        UpdateLayout();var image=new RenderTargetBitmap((int)Width,(int)Height,96,96,PixelFormats.Pbgra32);image.Render((Visual)Content);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(Program.Data,name)))encoder.Save(file);
        var pixels=new byte[(int)Width*(int)Height*4];image.CopyPixels(pixels,(int)Width*4,0);return pixels;
    }
    async Task VerifyUi() {
        var results=new List<string>();bool motion=living.Animate;bool onTop=settings.OnTop;double oldScale=ScaleValue(),oldSpeed=SpeedValue();
        Action<bool,string> check=(ok,name)=>{results.Add((ok?"PASS: ":"FAIL: ")+name);if(!ok)throw new Exception(name);};
        try {
            await Task.Delay(2200);check(tray!=null&&tray.Visible,"Tray icon exists");
            check(!ShowInTaskbar&&(Native.GetWindowLong(handle,-20)&0x80)!=0&&(Native.GetWindowLong(handle,-20)&0x40000)==0,"Hidden from taskbar and Alt+Tab");
            living.Animate=true;await Task.Delay(300);var before=Snapshot("widget-frame-1.png");int f=living.FrameCount;await Task.Delay(2400);var after=Snapshot("widget-frame-2.png");
            check(living.FrameCount>f+10,"Background continuously animates");
            int changed=0;for(int i=0;i<before.Length;i+=4)if(Math.Abs(before[i]-after[i])+Math.Abs(before[i+1]-after[i+1])+Math.Abs(before[i+2]-after[i+2])>10)changed++;
            check(changed>3000,"Rendered colors flow between frames");check(before[3]==0&&after[3]==0,"Rounded corners remain transparent");
            ((System.Windows.Forms.ToolStripMenuItem)tray.ContextMenuStrip.Items[1]).PerformClick();await Task.Delay(2400);
            check(!IsVisible&&!Native.IsWindowVisible(handle)&&tray.Visible,"Hide menu keeps tray alive without reopening widget");
            ((System.Windows.Forms.ToolStripMenuItem)tray.ContextMenuStrip.Items[0]).PerformClick();await Task.Delay(400);
            check(IsVisible&&Native.IsWindowVisible(handle)&&!ShowInTaskbar,"Show menu restores widget without taskbar button");
            Close();await Task.Delay(300);check(!IsVisible&&tray.Visible,"Closing window keeps tray process alive");ShowWidget();await Task.Delay(300);check(IsVisible,"Tray window reopens after close");
            ((System.Windows.Forms.ToolStripMenuItem)tray.ContextMenuStrip.Items[6]).PerformClick();await Task.Delay(300);check(!IsVisible&&tray.Visible,"Tray close keeps icon alive");((System.Windows.Forms.ToolStripMenuItem)tray.ContextMenuStrip.Items[0]).PerformClick();await Task.Delay(300);check(IsVisible,"Tray opens window after tray close");
            var prefs=(System.Windows.Forms.ToolStripMenuItem)tray.ContextMenuStrip.Items[2];var toggle=(System.Windows.Forms.ToolStripMenuItem)prefs.DropDownItems[1];toggle.Checked=true;toggle.PerformClick();await Task.Delay(2000);f=living.FrameCount;await Task.Delay(350);
            check(!living.Animate&&living.FrameCount==f,"Tray animation setting stops rendering");toggle.PerformClick();await Task.Delay(350);check(living.Animate&&living.FrameCount>f,"Tray animation setting resumes rendering");
            ((System.Windows.Forms.ToolStripMenuItem)((System.Windows.Forms.ToolStripMenuItem)prefs.DropDownItems[2]).DropDownItems[2]).PerformClick();check(Math.Abs(living.Speed-2)<.01,"Tray menu changes color speed");
            ((System.Windows.Forms.ToolStripMenuItem)((System.Windows.Forms.ToolStripMenuItem)prefs.DropDownItems[3]).DropDownItems[2]).PerformClick();await Task.Delay(400);check(Math.Abs(Width-500)<.01&&Math.Abs(Height-280)<.01,"Tray menu changes widget size");
            ((System.Windows.Forms.ToolStripMenuItem)((System.Windows.Forms.ToolStripMenuItem)prefs.DropDownItems[3]).DropDownItems[0]).PerformClick();await Task.Delay(400);var compact=Snapshot("widget-compact.png");check(Math.Abs(Width-320)<.01&&compact[(90*320+300)*4+3]>0,"Compact view keeps the right side visible");
            HideWidget();Program.ShowRequest.Set();await Task.Delay(600);check(IsVisible,"Repeated launch signal reopens hidden widget");
            settings.OnTop=false;ApplyLayer();await Task.Delay(300);check(parent!=IntPtr.Zero&&Native.IsWindowVisible(handle),"Desktop mode attaches successfully");HideWidget();await Task.Delay(2400);check(!Native.IsWindowVisible(handle),"Desktop mode stays hidden until requested");ShowWidget();
        } catch(Exception e) {results.Add("ERROR: "+e);Program.Log(e);}
        finally {settings.OnTop=onTop;living.Animate=motion;settings.AnimateBackground=motion;SetSpeed(oldSpeed);SetScale(oldScale);ApplyLayer();SaveSettings();File.WriteAllLines(Path.Combine(Program.Data,"ui-verification.txt"),results);Quit();}
    }
}
// Windows Core Audio session volume; this leaves all other applications alone.
static class AudioVolume {
    static ISimpleAudioVolume Find() {
        var enumerator=(IMMDeviceEnumerator)new MMDeviceEnumerator();IMMDevice device;enumerator.GetDefaultAudioEndpoint(0,1,out device);
        object managerObject;Guid id=typeof(IAudioSessionManager2).GUID;device.Activate(ref id,23,IntPtr.Zero,out managerObject);
        var manager=(IAudioSessionManager2)managerObject;IAudioSessionEnumerator sessions;manager.GetSessionEnumerator(out sessions);int count;sessions.GetCount(out count);
        var pids=Process.GetProcesses().Where(p=>p.ProcessName.IndexOf("Яндекс Музыка",StringComparison.OrdinalIgnoreCase)>=0).Select(p=>p.Id).ToArray();
        for(int i=0;i<count;i++){IAudioSessionControl control;sessions.GetSession(i,out control);var control2=(IAudioSessionControl2)control;uint pid;control2.GetProcessId(out pid);if(pids.Contains((int)pid))return (ISimpleAudioVolume)control;}
        return null;
    }
    internal static double? Get(){try{var v=Find();if(v==null)return null;float level;v.GetMasterVolume(out level);return level;}catch{return null;}}
    internal static bool Set(double level){try{var v=Find();if(v==null)return false;Guid context=Guid.Empty;v.SetMasterVolume((float)Math.Max(0,Math.Min(1,level)),ref context);return true;}catch{return false;}}
    [ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]class MMDeviceEnumerator{}
    [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IMMDeviceEnumerator{void EnumAudioEndpoints();void GetDefaultAudioEndpoint(int flow,int role,out IMMDevice device);}
    [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IMMDevice{void Activate(ref Guid id,int context,IntPtr paramsPtr,[MarshalAs(UnmanagedType.IUnknown)]out object value);}
    [ComImport,Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IAudioSessionManager2{void GetAudioSessionControl();void GetSimpleAudioVolume();void GetSessionEnumerator(out IAudioSessionEnumerator sessions);}
    [ComImport,Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IAudioSessionEnumerator{void GetCount(out int count);void GetSession(int index,out IAudioSessionControl session);}
    [ComImport,Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IAudioSessionControl {void GetState();void GetDisplayName();void SetDisplayName();void GetIconPath();void SetIconPath();void GetGroupingParam();void SetGroupingParam();void RegisterAudioSessionNotification();void UnregisterAudioSessionNotification();}
    [ComImport,Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IAudioSessionControl2 {void GetState();void GetDisplayName();void SetDisplayName();void GetIconPath();void SetIconPath();void GetGroupingParam();void SetGroupingParam();void RegisterAudioSessionNotification();void UnregisterAudioSessionNotification();void GetSessionIdentifier();void GetSessionInstanceIdentifier();void GetProcessId(out uint pid);}
    [ComImport,Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface ISimpleAudioVolume{void SetMasterVolume(float value,ref Guid context);void GetMasterVolume(out float value);void SetMute(bool mute,ref Guid context);void GetMute(out bool mute);}
}
class MenuColors : System.Windows.Forms.ProfessionalColorTable {
    public override System.Drawing.Color ToolStripDropDownBackground {get{return System.Drawing.Color.FromArgb(30,32,44);}}
    public override System.Drawing.Color MenuItemSelected {get{return System.Drawing.Color.FromArgb(69,63,101);}}
    public override System.Drawing.Color MenuItemBorder {get{return System.Drawing.Color.FromArgb(104,91,153);}}
    public override System.Drawing.Color MenuBorder {get{return System.Drawing.Color.FromArgb(104,91,153);}}
    public override System.Drawing.Color ImageMarginGradientBegin {get{return System.Drawing.Color.FromArgb(30,32,44);}}
    public override System.Drawing.Color ImageMarginGradientMiddle {get{return System.Drawing.Color.FromArgb(30,32,44);}}
    public override System.Drawing.Color ImageMarginGradientEnd {get{return System.Drawing.Color.FromArgb(30,32,44);}}
    public override System.Drawing.Color SeparatorDark {get{return System.Drawing.Color.FromArgb(90,83,117);}}
    public override System.Drawing.Color SeparatorLight {get{return System.Drawing.Color.FromArgb(90,83,117);}}
}
static class Native {
    [StructLayout(LayoutKind.Sequential)]internal struct POINT{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]internal struct RECT{public int Left,Top,Right,Bottom;}
    internal delegate bool EnumProc(IntPtr hwnd,IntPtr p);
    [DllImport("user32.dll")]internal static extern bool EnumWindows(EnumProc callback,IntPtr p);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern IntPtr FindWindowEx(IntPtr parent,IntPtr child,string className,string title);
    [DllImport("user32.dll")]internal static extern IntPtr SetParent(IntPtr hwnd,IntPtr parent);
    [DllImport("user32.dll")]internal static extern int GetWindowLong(IntPtr hwnd,int index);
    [DllImport("user32.dll")]internal static extern int SetWindowLong(IntPtr hwnd,int index,int value);
    [DllImport("user32.dll")]internal static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int cx,int cy,uint flags);
    [DllImport("user32.dll")]internal static extern bool ScreenToClient(IntPtr hwnd,ref POINT p);
    [DllImport("user32.dll")]internal static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]internal static extern bool GetWindowRect(IntPtr hwnd,out RECT r);
    [DllImport("user32.dll")]internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]internal static extern bool DestroyIcon(IntPtr icon);
    internal static IntPtr Desktop(){IntPtr found=IntPtr.Zero;EnumWindows((hwnd,p)=>{if(FindWindowEx(hwnd,IntPtr.Zero,"SHELLDLL_DefView",null)!=IntPtr.Zero){found=hwnd;return false;}return true;},IntPtr.Zero);return found;}
}
static class Tests {
    internal static void Run() {
        var checks=new List<string>();
        Action<bool,string> check=(ok,name)=>{if(!ok)throw new Exception("FAIL: "+name);checks.Add("PASS: "+name);};
        check(Media.IsYandex("ru.yandex.desktop.music")&&!Media.IsYandex("chrome.exe")&&!Media.IsYandex("Spotify.exe"),"Select only Yandex Music");
        check(Media.Time(65.9)=="1:05"&&Media.Time(-3)=="0:00"&&Media.Time(3605)=="1:00:05","Time formatting");
        var now=DateTimeOffset.UtcNow;
        check(Math.Abs(Media.Position(51,0,175,now.AddMinutes(-10),false,1)-51)<.01,"Paused position does not drift");
        check(Math.Abs(Media.Position(51,0,175,now.AddSeconds(-2),true,1)-53)<.2,"Playing position advances");
        check(Media.Position(174,0,175,now.AddSeconds(-10),true,1)==175,"Position clamped to track end");
        check(Media.Position(5,0,175,now.AddSeconds(10),true,1)==5,"Future timestamp cannot rewind");
        check(Media.Position(25,20,100,now,false,1)==5,"Nonzero timeline start");
        Func<byte,byte,byte,BitmapSource> solid=(r,g,b)=>BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,new byte[]{b,g,r,255,b,g,r,255,b,g,r,255,b,g,r,255},8);
        var red=Palette.Pick(solid(200,40,50));var blue=Palette.Pick(solid(30,60,210));
        check(red.R>red.B&&blue.B>blue.R,"Palette follows cover hue");
        check(Palette.Pick(solid(255,255,255)).A==255&&Palette.Pick(solid(0,0,0)).A==255,"White and black covers have valid fallback");
        var many=new byte[]{20,20,230,255,20,220,20,255,230,20,20,255,20,220,230,255,210,20,210,255,210,210,20,255,225,225,225,255,20,20,20,255};
        var palette=Palette.Extract(BitmapSource.Create(8,1,96,96,PixelFormats.Bgra32,null,many,32));
        check(palette.Length==8&&palette.Any(c=>c.R>180&&c.G<100)&&palette.Any(c=>c.G>180&&c.R<100)&&palette.Any(c=>c.B>180&&c.R<100),"Eight representative colors retain red, green and blue");
        check(Palette.Extract(solid(200,200,200)).All(c=>Math.Abs(c.R-c.G)<3&&Math.Abs(c.B-c.G)<3),"Monochrome artwork stays monochrome");
        check((LivingBackground.FieldPosition(0,0,400,224)-LivingBackground.FieldPosition(0,4,400,224)).Length>30,"Color fields move across the widget");
        check(Widget.ClickVolume(0,62)==0&&Math.Abs(Widget.ClickVolume(31,62)-50)<.01&&Widget.ClickVolume(62,62)==100,"Volume track clicks map directly to level");
        File.WriteAllLines(Path.Combine(Program.Data,"self-test.txt"),checks);
    }
}
}
