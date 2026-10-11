using System;
using System.Collections.Generic;
using System.Linq;
using Mycraft.Bridge;
using Mycraft.Demo;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Mycraft.UI;

public sealed class MinecraftHotbarSlotVM : ViewModel
{
    private string _sprite = "mycraft_item_unknown";
    private string _count = "";
    private bool _selected;
    private bool _hasItem;
    [DataSourceProperty] public string ItemSprite { get => _sprite; set { if (_sprite != value) { _sprite = value; OnPropertyChanged(nameof(ItemSprite)); } } }
    [DataSourceProperty] public string CountText { get => _count; set { if (_count != value) { _count = value; OnPropertyChanged(nameof(CountText)); } } }
    [DataSourceProperty] public bool IsSelected { get => _selected; set { if (_selected != value) { _selected = value; OnPropertyChanged(nameof(IsSelected)); } } }
    [DataSourceProperty] public bool HasItem { get => _hasItem; set { if (_hasItem != value) { _hasItem = value; OnPropertyChanged(nameof(HasItem)); } } }
}

public sealed class MinecraftMeterVM : ViewModel
{
    private string _sprite = "";
    [DataSourceProperty] public string SpriteName { get => _sprite; set { if (_sprite != value) { _sprite = value; OnPropertyChanged(nameof(SpriteName)); } } }
}

public sealed class MinecraftBossVM : ViewModel
{
    public string Id {get;}
    private string _text="";
    private float _width;
    public MinecraftBossVM(string id){Id=id;}
    [DataSourceProperty] public string BossText {get=>_text;set{if(_text!=value){_text=value;OnPropertyChanged(nameof(BossText));}}}
    [DataSourceProperty] public float FillWidth {get=>_width;set{if(Math.Abs(_width-value)>.001f){_width=value;OnPropertyChanged(nameof(FillWidth));}}}
    public void Refresh(McBossState state){BossText=state.Name+"  "+state.Hp.ToString("0.#")+" / "+state.MaxHp.ToString("0.#");FillWidth=(float)(546*state.Fraction);}
}

public sealed class MinecraftHotbarVM : ViewModel
{
    private readonly MBBindingList<MinecraftHotbarSlotVM> _slots = new MBBindingList<MinecraftHotbarSlotVM>();
    private readonly MBBindingList<MinecraftMeterVM> _hearts = new MBBindingList<MinecraftMeterVM>();
    private readonly MBBindingList<MinecraftMeterVM> _food = new MBBindingList<MinecraftMeterVM>();
    private readonly MBBindingList<MinecraftMeterVM> _armor = new MBBindingList<MinecraftMeterVM>();
    private readonly MBBindingList<MinecraftBossVM> _bosses = new MBBindingList<MinecraftBossVM>();
    private bool _visible;
    private bool _mcMode;
    private bool _hasArmor;
    private string _status = "";
    private bool _hasOffhand;
    private string _offhandSprite="mycraft_item_unknown";
    private string _offhandCount="";

    public MinecraftHotbarVM()
    {
        for (var i = 0; i < 9; i++) _slots.Add(new MinecraftHotbarSlotVM());
        for (var i = 0; i < 10; i++)
        {
            _hearts.Add(new MinecraftMeterVM());
            _food.Add(new MinecraftMeterVM());
            _armor.Add(new MinecraftMeterVM());
        }
    }

    [DataSourceProperty] public MBBindingList<MinecraftHotbarSlotVM> Slots => _slots;
    [DataSourceProperty] public MBBindingList<MinecraftMeterVM> Hearts => _hearts;
    [DataSourceProperty] public MBBindingList<MinecraftMeterVM> Food => _food;
    [DataSourceProperty] public MBBindingList<MinecraftMeterVM> Armor => _armor;
    [DataSourceProperty] public MBBindingList<MinecraftBossVM> Bosses => _bosses;
    [DataSourceProperty] public bool IsVisible { get => _visible; set { if (_visible != value) { _visible = value; OnPropertyChanged(nameof(IsVisible)); } } }
    [DataSourceProperty] public bool McMode { get => _mcMode; set { if (_mcMode != value) { _mcMode = value; OnPropertyChanged(nameof(McMode)); } } }
    [DataSourceProperty] public bool HasArmor { get => _hasArmor; set { if (_hasArmor != value) { _hasArmor = value; OnPropertyChanged(nameof(HasArmor)); } } }
    [DataSourceProperty] public string McStatusText { get => _status; set { if (_status != value) { _status = value; OnPropertyChanged(nameof(McStatusText)); } } }
    [DataSourceProperty] public bool HasOffhand {get=>_hasOffhand;set{if(_hasOffhand!=value){_hasOffhand=value;OnPropertyChanged(nameof(HasOffhand));}}}
    [DataSourceProperty] public string OffhandSprite {get=>_offhandSprite;set{if(_offhandSprite!=value){_offhandSprite=value;OnPropertyChanged(nameof(OffhandSprite));}}}
    [DataSourceProperty] public string OffhandCountText {get=>_offhandCount;set{if(_offhandCount!=value){_offhandCount=value;OnPropertyChanged(nameof(OffhandCountText));}}}

    public void Refresh(Agent agent)
    {
        var demo = DemoBridgeMissionBehavior.Current;
        if(demo?.OwnsPlayer(agent)!=true)
        {Hide();return;}
        IsVisible = true;
        McMode = true;
        var inventory = demo!.McHotbar;
        McStatusText = "Minecraft " + demo.McHp.ToString("0.#") + "/" + demo.McMaxHp.ToString("0.#");
        if(demo.CreatureVisualStatus.Length>0)McStatusText += " · "+demo.CreatureVisualStatus;
        RefreshBosses(demo.McBosses);
        FillMeter(_hearts, "heart", demo.McMaxHp > 0 ? demo.McHp / demo.McMaxHp * 20 : 0);
        FillMeter(_food, "food", inventory?.Food ?? 20);
        FillMeter(_armor, "armor", inventory?.Armor ?? 0);
        HasArmor = inventory?.Armor > 0;
        HasOffhand=inventory!=null && inventory.OffhandItem!="minecraft:air" && inventory.OffhandCount>0;
        OffhandSprite=HasOffhand?MinecraftHudSprites.ItemSprite(HudWeapon(inventory!.OffhandItem,demo,true)):"mycraft_item_unknown";
        OffhandCountText=inventory?.OffhandCount>1?inventory.OffhandCount.ToString():"";
        for (var i = 0; i < 9; i++)
            SetSlot(i, inventory?.Items[i] ?? "minecraft:air", inventory?.Counts[i] ?? 0, inventory?.SelectedSlot == i);
        if(inventory!=null && (demo.McWeaponKind=="bow" || demo.McWeaponKind=="crossbow"))
            _slots[inventory.SelectedSlot].ItemSprite=MinecraftHudSprites.ItemSprite(HudWeapon(demo.McItem,demo,false));
    }

    private static string HudWeapon(string item,DemoBridgeMissionBehavior demo,bool offhand)
    {
        if(item!="minecraft:bow" && item!="minecraft:crossbow")return item;
        return "minecraft:"+MinecraftWeaponPresentation.Sprite(item,demo.McUsingItem && demo.McUsedHand==(offhand?"offhand":"mainhand"),
            demo.McUseTicks,demo.McCrossbowCharge,offhand?demo.McOffhandCrossbowLoaded:demo.McCrossbowLoaded,
            offhand?demo.McOffhandCrossbowFirework:demo.McCrossbowFirework);
    }

    private void SetSlot(int i, string item, int count, bool selected)
    {
        var slot = _slots[i];
        slot.HasItem = item != "minecraft:air" && count > 0;
        slot.ItemSprite = slot.HasItem ? MinecraftHudSprites.ItemSprite(item) : "mycraft_item_unknown";
        slot.CountText = count > 1 ? count.ToString() : "";
        slot.IsSelected = selected;
    }

    private static void FillMeter(MBBindingList<MinecraftMeterVM> meters, string kind, float value)
    {
        var units = Math.Max(0, Math.Min(20, (int)Math.Ceiling(value)));
        for (var i = 0; i < 10; i++)
            meters[i].SpriteName = "mycraft_" + kind + (units >= i * 2 + 2 ? "_full" : units == i * 2 + 1 ? "_half" : "_empty");
    }

    private void RefreshBosses(IEnumerable<McBossState> states)
    {
        var current=states.Take(16).ToDictionary(s=>s.Id,StringComparer.Ordinal);
        for(int i=_bosses.Count-1;i>=0;i--)if(!current.ContainsKey(_bosses[i].Id)){_bosses[i].OnFinalize();_bosses.RemoveAt(i);}
        foreach(var state in current.Values)
        {
            var vm=_bosses.FirstOrDefault(v=>v.Id==state.Id);
            if(vm==null){vm=new MinecraftBossVM(state.Id);_bosses.Add(vm);}
            vm.Refresh(state);
        }
    }
    public void Hide() { IsVisible=false; McMode=false; foreach(var boss in _bosses)boss.OnFinalize();_bosses.Clear(); }
    public override void OnFinalize()
    {
        foreach (var slot in _slots) slot.OnFinalize();
        foreach (var meter in _hearts) meter.OnFinalize();
        foreach (var meter in _food) meter.OnFinalize();
        foreach (var meter in _armor) meter.OnFinalize();
        foreach (var boss in _bosses) boss.OnFinalize();
        _slots.Clear(); _hearts.Clear(); _food.Clear(); _armor.Clear(); _bosses.Clear();
        base.OnFinalize();
    }
}
