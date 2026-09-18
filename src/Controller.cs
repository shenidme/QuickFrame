using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

class Controller : Form {
    readonly Overlay overlay=new Overlay();
    readonly Stopwatch clock=Stopwatch.StartNew();
    readonly Timer timer=new Timer { Interval=33 };
    readonly NotifyIcon tray=new NotifyIcon();
    readonly Icon trayIcon=Rainbow.TrayIcon();
    readonly MouseInput input;
    readonly string settingsPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings.ini");
    readonly bool persistSettings;
    SavedSettings settings=new SavedSettings();
    SettingsWindow settingsWindow;
    bool saveErrorShown,disposed;
    public Controller(bool smoke) {
        persistSettings=!smoke;
        if(persistSettings) {
            try { settings=SavedSettings.Load(settingsPath); } catch(IOException) { } catch(UnauthorizedAccessException) { }
            try { settings.AutoStart=StartupManager.IsEnabled(Application.ExecutablePath); } catch(System.Security.SecurityException) { settings.AutoStart=false; } catch(UnauthorizedAccessException) { settings.AutoStart=false; }
        }
        Text="QuickFrame"; ShowInTaskbar=false; var handle=Handle;
        input=new MouseInput(clock); ApplyRuntime();
        var menu=new ContextMenuStrip();
        var title=new ToolStripMenuItem("QuickFrame") { Enabled=false }; menu.Items.Add(title);
        menu.Items.Add("设置与预览…",null,delegate { OpenSettings(); });
        var toggle=new ToolStripMenuItem(); toggle.Click+=delegate { Toggle(); }; menu.Items.Add(toggle);
        var styles=new ToolStripMenuItem("外观预设"); menu.Items.Add(styles);
        for(int i=0;i<12;i++) {
            int index=i; var item=new ToolStripMenuItem(Presets.Name(i)); styles.DropDownItems.Add(item);
            item.Click+=delegate { var next=settings.Copy(); Presets.Select(next,index); ApplySettings(next); };
        }
        var times=new ToolStripMenuItem("渐隐前停留时间"); menu.Items.Add(times);
        foreach(int ms in new int[]{0,1000,1500,2000,3000}) {
            int hold=ms; var item=new ToolStripMenuItem(ms==0?"无停留（松开即渐隐）":(ms/1000.0).ToString("0.0")+" 秒") { Tag=ms };
            item.Click+=delegate { var next=settings.Copy(); next.Duration=hold; ApplySettings(next); }; times.DropDownItems.Add(item);
        }
        var focus=new ToolStripMenuItem("聚焦遮罩 · 框外渐暗"); menu.Items.Add(focus);
        focus.Click+=delegate { var next=settings.Copy(); next.Focus=!next.Focus; ApplySettings(next); };
        var startup=new ToolStripMenuItem("开机自启动"); menu.Items.Add(startup);
        startup.Click+=delegate { var next=settings.Copy(); next.AutoStart=!next.AutoStart; ApplySettings(next); };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("清除所有框 · Esc",null,delegate { Clear(); });
        menu.Items.Add("修复鼠标监听",null,delegate { input.Repair(); Clear(); tray.ShowBalloonTip(2000,"QuickFrame","正在重新连接鼠标监听。",ToolTipIcon.Info); });
        menu.Items.Add("退出",null,delegate { Application.Exit(); });
        menu.Opening+=delegate {
            title.Text="QuickFrame · "+Presets.TriggerNames[(int)settings.Trigger]; toggle.Text=settings.Enabled?"暂停画框":"恢复画框";
            focus.Checked=settings.Focus; startup.Checked=settings.AutoStart;
            for(int i=0;i<12;i++) ((ToolStripMenuItem)styles.DropDownItems[i]).Checked=i==Presets.Index(settings);
            foreach(ToolStripMenuItem item in times.DropDownItems) item.Checked=(int)item.Tag==settings.Duration;
        };
        tray.Icon=trayIcon; tray.ContextMenuStrip=menu; tray.DoubleClick+=delegate { Toggle(); }; tray.Visible=true; UpdateTray();
        bool hotkey=Native.RegisterHotKey(Handle,1,0x4003,(uint)Keys.F8);
        SystemEvents.DisplaySettingsChanged+=DisplayChanged; SystemEvents.PowerModeChanged+=PowerChanged;
        bool shadeWasShown=false;
        timer.Tick+=delegate {
            if(Program.ShowRequest!=null && Program.ShowRequest.WaitOne(0)) tray.ShowBalloonTip(2000,"QuickFrame 已在运行","右键点击方框托盘图标 → 设置与预览。",ToolTipIcon.Info);
            if(Program.SettingsRequest!=null && Program.SettingsRequest.WaitOne(0)) OpenSettings();
            overlay.Now=clock.ElapsedMilliseconds;
            var snapshot=input.Read();
            if(snapshot.Clear) { overlay.Frames.Clear(); overlay.Preview=null; }
            overlay.Preview=snapshot.Preview; overlay.PreviewBorn=snapshot.Born; overlay.PreviewColor=settings.Color; overlay.PreviewStyle=settings.Style;
            foreach(var stroke in snapshot.Completed) overlay.Frames.Add(new Frame { Rect=stroke.Rect,Color=settings.Color,Style=settings.Style,Born=stroke.Born,Until=stroke.Released+settings.Duration+settings.Fade,FadeDuration=settings.Fade });
            overlay.Frames.RemoveAll(f=>f.Until<=overlay.Now);
            if(overlay.Frames.Count>20) overlay.Frames.RemoveRange(0,overlay.Frames.Count-20);
            input.CaptureEscape=overlay.Preview.HasValue || overlay.Frames.Count>0;
            if(overlay.Visible || overlay.Frames.Count>0 || overlay.Preview.HasValue) overlay.RefreshFrames();
            if(overlay.ShadeVisible) shadeWasShown=true;
        };
        timer.Start();
        if(smoke) {
            overlay.FocusEnabled=true;
            foreach(FrameStyle sample in Enum.GetValues(typeof(FrameStyle))) overlay.Frames.Add(new Frame { Rect=new Rectangle(30+((int)sample%3)*200,30+((int)sample/3)*130,170,95),Style=sample,Born=0,Until=700 });
            overlay.RefreshFrames();
            var finish=new Timer { Interval=1100 };
            finish.Tick+=delegate {
                finish.Stop(); finish.Dispose();
                Program.SmokePassed=shadeWasShown && overlay.Frames.Count==0 && !overlay.Visible && !overlay.ShadeVisible && input.Installed && !Visible && !ShowInTaskbar && tray.Visible && overlay.Allocations==1;
                Application.Exit();
            }; finish.Start();
        } else {
            SaveSettings();
            tray.ShowBalloonTip(3000,"QuickFrame 已启动",Presets.TriggerNames[(int)settings.Trigger]+"画框。右键托盘可打开设置。"+(hotkey?"\nCtrl+Alt+F8 暂停或恢复；Esc 清框。":"\n暂停快捷键被占用，请使用托盘菜单。"),ToolTipIcon.Info);
        }
    }
    void ApplyRuntime() {
        input.Configure(settings.Trigger,settings.ExcludedApps); input.SetEnabled(settings.Enabled);
        overlay.FocusEnabled=settings.Focus; overlay.CurrentScreen=settings.CurrentScreen; overlay.Dim=settings.Dim; overlay.Speed=settings.Speed;
        Rainbow.WidthScale=settings.Width/5f; Rainbow.GlowScale=settings.Glow/100f;
    }
    void OpenSettings() {
        if(settingsWindow!=null && !settingsWindow.IsDisposed) { settingsWindow.Activate(); return; }
        settingsWindow=new SettingsWindow(settings,ApplySettings); settingsWindow.Show();
    }
    bool ApplySettings(SavedSettings next) {
        bool oldStartup=settings.AutoStart,changedStartup=next.AutoStart!=oldStartup;
        try {
            if(changedStartup && persistSettings) StartupManager.SetEnabled(next.AutoStart,Application.ExecutablePath);
            if(persistSettings) next.Save(settingsPath);
        } catch(Exception e) {
            if(!(e is IOException) && !(e is UnauthorizedAccessException) && !(e is System.Security.SecurityException)) throw;
            if(changedStartup && persistSettings) try { StartupManager.SetEnabled(oldStartup,Application.ExecutablePath); } catch { }
            MessageBox.Show("设置未能保存："+e.Message,"QuickFrame",MessageBoxButtons.OK,MessageBoxIcon.Warning); return false;
        }
        bool inputChanged=settings.Trigger!=next.Trigger || settings.Enabled!=next.Enabled || settings.ExcludedApps!=next.ExcludedApps;
        settings=next; if(inputChanged) Clear();
        ApplyRuntime(); UpdateTray(); overlay.RefreshFrames(); return true;
    }
    void Clear() { input.Cancel(); input.CaptureEscape=false; overlay.Frames.Clear(); overlay.Preview=null; overlay.RefreshFrames(); }
    void Toggle() { var next=settings.Copy(); next.Enabled=!next.Enabled; ApplySettings(next); }
    void UpdateTray() { tray.Text=settings.Enabled?"QuickFrame · "+Presets.TriggerNames[(int)settings.Trigger]:"QuickFrame · 已暂停"; }
    void Recover() {
        if(disposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(delegate { if(!disposed) { Clear(); input.Repair(); } })); } catch(InvalidOperationException) { }
    }
    void DisplayChanged(object sender,EventArgs e) { Recover(); }
    void PowerChanged(object sender,PowerModeChangedEventArgs e) { if(e.Mode==PowerModes.Resume) Recover(); }
    void SaveSettings() {
        if(!persistSettings) return;
        try { settings.Save(settingsPath); saveErrorShown=false; }
        catch(IOException e) { SaveError(e); } catch(UnauthorizedAccessException e) { SaveError(e); }
    }
    void SaveError(Exception e) { if(!saveErrorShown) tray.ShowBalloonTip(3000,"QuickFrame 设置未能保存","请将程序放到可写目录。"+e.Message,ToolTipIcon.Warning); saveErrorShown=true; }
    protected override void OnFormClosing(FormClosingEventArgs e) { if(e.CloseReason==CloseReason.UserClosing) { e.Cancel=true; Hide(); } base.OnFormClosing(e); }
    protected override void WndProc(ref Message m) { if(m.Msg==0x312 && m.WParam.ToInt32()==1) Toggle(); base.WndProc(ref m); }
    protected override void Dispose(bool disposing) {
        if(disposing && !disposed) {
            disposed=true; SaveSettings(); SystemEvents.DisplaySettingsChanged-=DisplayChanged; SystemEvents.PowerModeChanged-=PowerChanged;
            Native.UnregisterHotKey(Handle,1); timer.Dispose(); if(settingsWindow!=null) settingsWindow.Dispose();
            input.Dispose(); tray.Visible=false; tray.Dispose(); trayIcon.Dispose(); overlay.Dispose();
        }
        base.Dispose(disposing);
    }
}
