using System;
using System.Collections.Generic;
using Mycraft.Bridge;
using Mycraft.Demo;
using Mycraft.Patches;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;
using TaleWorlds.InputSystem;

namespace Mycraft.UI;

public sealed class MinecraftInventoryOverlay:IDisposable
{
    // ScreenLayer.Update receives committed Unicode input from the engine (including IME).
    private sealed class InventoryLayer:GauntletLayer
    {
        private readonly Action<IReadOnlyList<int>> _characters;
        public InventoryLayer(Action<IReadOnlyList<int>> characters):base("Mycraft.Inventory",200,shouldClear:false){_characters=characters;}
        protected override void Update(IReadOnlyList<int> lastKeysPressed){_characters(lastKeysPressed);}
    }
    private ScreenBase? _screen;
    private InventoryLayer? _layer;
    private GauntletMovieIdentifier? _movie;
    private MinecraftInventoryVM? _vm;
    private VanillaInventoryTransport? _transport;
    private MinecraftInventoryTexture? _texture;
    private bool _openedThisTick;
    private bool _layerAdded;
    private long _openedAt;
    private long _pendingDisplayFrame;
    private int _width,_height;
    private double _lastX=.5,_lastY=.5;
    private readonly bool[] _buttons=new bool[3];
    private readonly Dictionary<InputKey,long> _repeats=new Dictionary<InputKey,long>();
    private static readonly InputKey[] MouseKeys={InputKey.LeftMouseButton,InputKey.RightMouseButton,InputKey.MiddleMouseButton};
    private static readonly Dictionary<InputKey,int> Keys=CreateKeys();
    public bool IsOpen=>_layer!=null;
    public bool ClosedThisTick{get;private set;}
    public void Open(string sessionId)
    {
        if(IsOpen)return;
        _screen=ScreenManager.TopScreen;
        if(_screen==null)return;
        try
        {
            _vm=new MinecraftInventoryVM();
            _layer=new InventoryLayer(Characters);
            _texture=new MinecraftInventoryTexture();
            _movie=_layer.LoadMovie("MCInventory",_vm);
            _layer.InputRestrictions.SetInputRestrictions(true,InputUsageMask.All);
            _layer.IsFocusLayer=true;
            _screen.AddLayer(_layer);_layerAdded=true;
            ScreenManager.TrySetFocus(_layer);
            _transport=new VanillaInventoryTransport(sessionId);
            _openedThisTick=true;_openedAt=VanillaInventoryTransport.Now();
            DemoLog.Info("native inventory overlay opened: "+sessionId);
        }
        catch(Exception e){MinecraftHeroState.LogFailure("MC vanilla inventory UI",e);Dispose();}
    }
    public void Tick()
    {
        ClosedThisTick=false;
        try { TickCore(); }
        catch(Exception e)
        {
            MinecraftHeroState.LogFailure("native inventory overlay (local close only)",e);
            Dispose();
        }
    }
    private void TickCore()
    {
        if(_layer==null || _transport==null || _vm==null)return;
        if(!ReferenceEquals(_screen,ScreenManager.TopScreen)){Dispose();return;}
        if(_openedThisTick){_openedThisTick=false;return;}
        long now=VanillaInventoryTransport.Now();
        if(_transport.RemoteClosed){Dispose();return;}
        if(_transport.Failed || now-Math.Max(_openedAt,_transport.LastFrameTime)>8000)
        {
            DemoLog.Info("native inventory overlay closed locally: failed="+_transport.Failed+", lastFrameAgeMs="+(now-Math.Max(_openedAt,_transport.LastFrameTime)));
            InformationManager.DisplayMessage(new InformationMessage("Minecraft 原版背包连接中断，请确认 MC 客户端仍在世界中。"));
            Dispose();return;
        }
        // Input sampled now was made against the previous UI render, not the frame uploaded below.
        _transport.MarkDisplayed(_pendingDisplayFrame);
        var frame=_transport.TakeFrame();
        if(frame!=null)
        {
            if(_pendingDisplayFrame==0)DemoLog.Info("native inventory first host frame: "+frame.Width+"x"+frame.Height);
            _vm.FrameSprite=_texture!.Update(frame);
            _width=frame.Width;_height=frame.Height;_vm.Ready=true;
            _pendingDisplayFrame=frame.Number;
        }
        if(!_vm.Ready || now-_transport.LastFrameTime>3000)
        {
            _vm.Ready=false;
            if(_layer.Input.IsKeyPressed(InputKey.Escape)){Dispose();return;}
            return;
        }
        var resolution=Input.Resolution;
        double fit=Math.Min((double)resolution.X/_width,(double)resolution.Y/_height);
        double displayW=_width*fit,displayH=_height*fit;
        _vm.FrameWidth=(float)(displayW/_layer.UIContext.Scale);
        _vm.FrameHeight=(float)(displayH/_layer.UIContext.Scale);
        var pointer=_layer.Input.GetMousePositionPixel();
        double x=Math.Max(-.1,Math.Min(1.1,(pointer.X-(resolution.X-displayW)/2)/displayW));
        double y=Math.Max(-.1,Math.Min(1.1,(pointer.Y-(resolution.Y-displayH)/2)/displayH));
        int mods=(_layer.Input.IsShiftDown()?1:0)|(_layer.Input.IsControlDown()?2:0)|(_layer.Input.IsAltDown()?4:0);
        bool moved=Math.Abs(x-_lastX)>.0001 || Math.Abs(y-_lastY)>.0001;
        if(moved)Send("move",x,y,mods);
        for(int b=0;b<3;b++)
        {
            bool down=_layer.Input.IsKeyDown(MouseKeys[b]);
            if(down && !_buttons[b])Send("down",x,y,mods,b);
            else if(!down && _buttons[b])Send("up",x,y,mods,b);
            else if(down && moved)
            {
                var e=MouseEvent("drag",x,y,mods,b);e["dx"]=x-_lastX;e["dy"]=y-_lastY;_transport.Enqueue(e);
            }
            _buttons[b]=down;
        }
        _lastX=x;_lastY=y;
        float scroll=_layer.Input.GetDeltaMouseScroll();
        if(Math.Abs(scroll)>.001f){var e=MouseEvent("scroll",x,y,mods);e["amount"]=(double)Math.Sign(scroll);_transport.Enqueue(e);}
        foreach(var pair in Keys)
        {
            if(_layer.Input.IsKeyPressed(pair.Key))
            {
                _transport.Enqueue(new Dictionary<string,object>{{"type","key"},{"key",pair.Value},{"mods",mods}});
                _repeats[pair.Key]=now+400;
            }
            else if(_layer.Input.IsKeyReleased(pair.Key))
            {
                _transport.Enqueue(new Dictionary<string,object>{{"type","release"},{"key",pair.Value},{"mods",mods}});
                _repeats.Remove(pair.Key);
            }
            else if(IsRepeatKey(pair.Key) && _layer.Input.IsKeyDown(pair.Key) && _repeats.TryGetValue(pair.Key,out long next) && now>=next)
            {
                _transport.Enqueue(new Dictionary<string,object>{{"type","key"},{"key",pair.Value},{"mods",mods}});
                _repeats[pair.Key]=now+50;
            }
        }
    }
    private void Characters(IReadOnlyList<int> points)
    {
        if(_openedThisTick || _vm?.Ready!=true || _transport==null)return;
        foreach(int point in points)
            if(point>=32 && point<=0x10ffff && (point<0xd800 || point>0xdfff))
                _transport.Enqueue(new Dictionary<string,object>{{"type","char"},{"codepoint",point}});
    }
    private static Dictionary<string,object> MouseEvent(string type,double x,double y,int mods,int button=0)=>
        new Dictionary<string,object>{{"type",type},{"x",x},{"y",y},{"mods",mods},{"button",button}};
    private void Send(string type,double x,double y,int mods,int button=0)=>_transport!.Enqueue(MouseEvent(type,x,y,mods,button));
    private static bool IsRepeatKey(InputKey key)=>key==InputKey.BackSpace || key==InputKey.Delete || key==InputKey.Left || key==InputKey.Right || key==InputKey.Up || key==InputKey.Down;
    private static Dictionary<InputKey,int> CreateKeys()
    {
        var keys=new Dictionary<InputKey,int>();
        for(char c='A';c<='Z';c++)keys[(InputKey)Enum.Parse(typeof(InputKey),c.ToString())]=c;
        for(int i=0;i<=9;i++)keys[(InputKey)Enum.Parse(typeof(InputKey),"D"+i)]=48+i;
        keys[InputKey.Space]=32;keys[InputKey.Escape]=256;keys[InputKey.Enter]=257;keys[InputKey.Tab]=258;keys[InputKey.BackSpace]=259;
        keys[InputKey.Insert]=260;keys[InputKey.Delete]=261;keys[InputKey.Right]=262;keys[InputKey.Left]=263;keys[InputKey.Down]=264;keys[InputKey.Up]=265;
        keys[InputKey.PageUp]=266;keys[InputKey.PageDown]=267;keys[InputKey.Home]=268;keys[InputKey.End]=269;
        keys[InputKey.Apostrophe]=39;keys[InputKey.Comma]=44;keys[InputKey.Minus]=45;keys[InputKey.Period]=46;keys[InputKey.Slash]=47;
        keys[InputKey.SemiColon]=59;keys[InputKey.Equals]=61;keys[InputKey.OpenBraces]=91;keys[InputKey.BackSlash]=92;keys[InputKey.CloseBraces]=93;keys[InputKey.Tilde]=96;
        return keys;
    }
    // Legacy state/results are still used by the HUD; this screen has no copy or swap model.
    public void Apply(McInventoryState state){}
    public void Status(string message){}
    public void Dispose()
    {
        if(_layer!=null)ClosedThisTick=true;
        _transport?.Dispose();_transport=null;
        var layer=_layer;var movie=_movie;var screen=_screen;var vm=_vm;
        _layer=null;_movie=null;_screen=null;_vm=null;
        if(layer!=null)
        {
            Cleanup(()=>layer.InputRestrictions.ResetInputRestrictions());
            layer.IsFocusLayer=false;
            Cleanup(()=>ScreenManager.TryLoseFocus(layer));
            if(movie!=null)Cleanup(()=>layer.ReleaseMovie(movie));
            if(_layerAdded && screen?.HasLayer(layer)==true)Cleanup(()=>screen.RemoveLayer(layer));
            DemoLog.Info("native inventory overlay disposed; host focus released");
        }
        _layerAdded=false;
        var texture=_texture;_texture=null;
        if(texture!=null)Cleanup(texture.Dispose);
        Array.Clear(_buttons,0,_buttons.Length);_repeats.Clear();_width=0;_height=0;_pendingDisplayFrame=0;
        if(vm!=null)Cleanup(vm.OnFinalize);
    }
    private static void Cleanup(Action action)
    {
        try{action();}catch(Exception e){MinecraftHeroState.LogFailure("native inventory cleanup",e);}
    }
}
