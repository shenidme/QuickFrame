using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

sealed class PreviewPanel : Panel {
    public SavedSettings Settings;
    readonly Stopwatch clock=Stopwatch.StartNew();
    readonly Timer timer=new Timer { Interval=40 };
    Bitmap canvas;
    public PreviewPanel() { DoubleBuffered=true; BackColor=Color.FromArgb(24,29,40); timer.Tick+=delegate { Invalidate(); }; timer.Start(); }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e); if(Settings==null) return;
        int width=Math.Max(1,(int)(Width*96/e.Graphics.DpiX)),height=Math.Max(1,(int)(Height*96/e.Graphics.DpiY));
        if(canvas==null || canvas.Width!=width || canvas.Height!=height) { if(canvas!=null) canvas.Dispose(); canvas=new Bitmap(width,height); canvas.SetResolution(96,96); }
        using(var g=Graphics.FromImage(canvas)) Render(g,width,height);
        e.Graphics.DrawImage(canvas,ClientRectangle);
    }
    void Render(Graphics g,int width,int height) {
        g.Clear(BackColor);
        using(var grid=new Pen(Color.FromArgb(35,43,59))) {
            for(int x=0;x<width;x+=28) g.DrawLine(grid,x,0,x,height);
            for(int y=0;y<height;y+=28) g.DrawLine(grid,0,y,width,y);
        }
        Rectangle r=new Rectangle(65,45,width-130,height-90);
        long cycle=2800+Settings.Duration+Settings.Fade,age=clock.ElapsedMilliseconds%cycle;
        var f=new Frame { Until=cycle,FadeDuration=Settings.Fade };
        float alpha=f.Opacity(age);
        if(Settings.Focus) using(var region=FocusShade.Mask(new Rectangle(0,0,width,height),new Rectangle[]{r}))
            using(var fill=new SolidBrush(Color.FromArgb((int)(255*FocusShade.Strength(age,alpha)*Settings.Dim/46.0),Color.Black))) g.FillRegion(fill,region);
        float oldWidth=Rainbow.WidthScale,oldGlow=Rainbow.GlowScale;
        try {
            Rainbow.WidthScale=Settings.Width/5f; Rainbow.GlowScale=Settings.Glow/100f;
            double phase=-clock.Elapsed.TotalSeconds/Settings.Speed;
            if(Settings.Style==FrameStyle.NightCity) Rainbow.NightCity(g,r,Rainbow.Deploy(age),Math.Max(0,1-(cycle-age)/(float)Settings.Fade),alpha,phase);
            else Rainbow.Glow(g,r,phase,alpha,Settings.Color,Settings.Style);
        } finally { Rainbow.WidthScale=oldWidth; Rainbow.GlowScale=oldGlow; }
        using(var font=new Font("Microsoft YaHei UI",9)) using(var brush=new SolidBrush(Color.FromArgb(192,206,229)))
            using(var format=new StringFormat { Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center })
                g.DrawString("预览 · 按住 → 松开 → 消散",font,brush,new Rectangle(0,0,width,height),format);
    }
    protected override void Dispose(bool disposing) { if(disposing) { timer.Dispose(); if(canvas!=null) canvas.Dispose(); } base.Dispose(disposing); }
}

sealed class SettingsWindow : Form {
    readonly Func<SavedSettings,bool> apply;
    SavedSettings value;
    readonly ComboBox preset=new ComboBox(),trigger=new ComboBox(),scope=new ComboBox();
    readonly CheckBox focus=new CheckBox(),startup=new CheckBox();
    readonly NumericUpDown hold=new NumericUpDown(),fade=new NumericUpDown(),width=new NumericUpDown(),glow=new NumericUpDown(),speed=new NumericUpDown(),dim=new NumericUpDown();
    readonly TextBox exclude=new TextBox();
    readonly PreviewPanel preview=new PreviewPanel();
    bool loading;
    public SettingsWindow(SavedSettings settings,Func<SavedSettings,bool> onApply) {
        SuspendLayout();
        apply=onApply; value=settings.Copy();
        Text="QuickFrame · 设置"; Font=new Font("Microsoft YaHei UI",9);
        AutoScaleDimensions=new SizeF(96,96); AutoScaleMode=AutoScaleMode.Dpi;
        ClientSize=new Size(640,650); FormBorderStyle=FormBorderStyle.FixedDialog; MaximizeBox=false; MinimizeBox=false;
        StartPosition=FormStartPosition.CenterScreen;
        preset.Name="preset"; trigger.Name="trigger"; scope.Name="scope"; hold.Name="hold"; fade.Name="fade";
        width.Name="width"; glow.Name="glow"; speed.Name="speed"; dim.Name="dim"; focus.Name="focus"; startup.Name="startup"; exclude.Name="exclude";
        preview.SetBounds(16,16,608,195); Controls.Add(preview);
        AddLabel("外观预设",20,230); SetupCombo(preset,155,225,465);
        for(int i=0;i<12;i++) preset.Items.Add(Presets.Name(i));
        AddLabel("画框操作",20,273); SetupCombo(trigger,155,268,465); trigger.Items.AddRange(Presets.TriggerNames);
        AddLabel("松开后停留（秒）",20,316); SetupNumber(hold,155,311,0,3,0.1m,2);
        AddLabel("渐隐（秒）",337,316); SetupNumber(fade,475,311,0.15m,1.5m,0.05m,2);
        AddLabel("边框粗细（像素）",20,359); SetupNumber(width,155,354,3,9,1,0);
        AddLabel("辉光强度（%）",337,359); SetupNumber(glow,475,354,0,150,10,0);
        AddLabel("色彩循环（秒）",20,402); SetupNumber(speed,155,397,3,20,1,0);
        AddLabel("遮罩深度（%）",337,402); SetupNumber(dim,475,397,10,80,5,0);
        focus.Text="启用框外渐暗"; focus.SetBounds(20,440,180,28); Controls.Add(focus);
        SetupCombo(scope,215,438,405); scope.Items.AddRange(new string[]{"遮罩范围 · 所有显示器","遮罩范围 · 最近画框所在显示器"});
        AddLabel("排除应用",20,485); exclude.SetBounds(155,480,465,28); Controls.Add(exclude);
        AddLabel("填写进程名，用分号分隔，例如 chrome.exe; game.exe",155,514,465);
        startup.Text="登录 Windows 时自动启动"; startup.SetBounds(20,550,320,30); Controls.Add(startup);
        var reset=new Button { Text="恢复默认",Name="reset" }; reset.SetBounds(20,603,115,32); reset.Click+=delegate { LoadValues(new SavedSettings()); }; Controls.Add(reset);
        var save=new Button { Text="保存设置",Name="save" }; save.SetBounds(380,603,115,32); save.Click+=delegate { if(apply(ReadValues())) Close(); }; Controls.Add(save); AcceptButton=save;
        var cancel=new Button { Text="取消",Name="cancel",DialogResult=DialogResult.Cancel }; cancel.SetBounds(505,603,115,32); cancel.Click+=delegate { Close(); }; Controls.Add(cancel); CancelButton=cancel;
        foreach(var number in new NumericUpDown[]{hold,fade,width,glow,speed,dim}) number.ValueChanged+=Changed;
        preset.SelectedIndexChanged+=Changed; trigger.SelectedIndexChanged+=Changed; scope.SelectedIndexChanged+=Changed;
        focus.CheckedChanged+=Changed; startup.CheckedChanged+=Changed; exclude.TextChanged+=Changed;
        LoadValues(settings);
        ResumeLayout(true);
    }
    void AddLabel(string text,int x,int y,int w=135) { Controls.Add(new Label { Text=text,Location=new Point(x,y),Size=new Size(w,25) }); }
    void SetupCombo(ComboBox box,int x,int y,int w) {
        box.DropDownStyle=ComboBoxStyle.DropDownList;
        box.SetBounds(x,y,w,28); Controls.Add(box);
    }
    void SetupNumber(NumericUpDown box,int x,int y,decimal min,decimal max,decimal step,int digits) { box.Minimum=min; box.Maximum=max; box.Increment=step; box.DecimalPlaces=digits; box.SetBounds(x,y,145,28); Controls.Add(box); }
    public void LoadValues(SavedSettings settings) {
        loading=true; value=settings.Copy(); preset.SelectedIndex=Presets.Index(value); trigger.SelectedIndex=(int)value.Trigger;
        hold.Value=value.Duration/1000m; fade.Value=value.Fade/1000m; width.Value=value.Width; glow.Value=value.Glow; speed.Value=value.Speed; dim.Value=value.Dim;
        focus.Checked=value.Focus; startup.Checked=value.AutoStart; scope.SelectedIndex=value.CurrentScreen?1:0; exclude.Text=value.ExcludedApps;
        loading=false; Changed(this,EventArgs.Empty);
    }
    SavedSettings ReadValues() {
        var s=value.Copy(); Presets.Select(s,preset.SelectedIndex); s.Trigger=(TriggerMode)trigger.SelectedIndex;
        s.Duration=(int)(hold.Value*1000); s.Fade=(int)(fade.Value*1000); s.Width=(int)width.Value; s.Glow=(int)glow.Value;
        s.Speed=(int)speed.Value; s.Dim=(int)dim.Value; s.Focus=focus.Checked; s.AutoStart=startup.Checked;
        s.CurrentScreen=scope.SelectedIndex==1; s.ExcludedApps=exclude.Text.Trim(); return s;
    }
    void Changed(object sender,EventArgs e) {
        if(loading) return; preview.Settings=ReadValues(); preview.Invalidate(); scope.Enabled=dim.Enabled=focus.Checked;
    }
}
