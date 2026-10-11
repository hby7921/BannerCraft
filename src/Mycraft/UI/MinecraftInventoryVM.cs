using System;
using TaleWorlds.Library;

namespace Mycraft.UI;

/// <summary>The inventory content is rendered by Minecraft itself.</summary>
public sealed class MinecraftInventoryVM : ViewModel
{
    private string _sprite="BlankWhiteSquare",_status="正在打开 Minecraft 原版背包…";
    private bool _ready;
    private float _width=1,_height=1;
    [DataSourceProperty] public string FrameSprite{get=>_sprite;set{if(_sprite!=value){_sprite=value;OnPropertyChanged(nameof(FrameSprite));}}}
    [DataSourceProperty] public string Status{get=>_status;set{if(_status!=value){_status=value;OnPropertyChanged(nameof(Status));}}}
    [DataSourceProperty] public bool Ready{get=>_ready;set{if(_ready!=value){_ready=value;OnPropertyChanged(nameof(Ready));OnPropertyChanged(nameof(Loading));}}}
    [DataSourceProperty] public bool Loading=>!Ready;
    [DataSourceProperty] public float FrameWidth{get=>_width;set{if(Math.Abs(_width-value)>.001f){_width=value;OnPropertyChanged(nameof(FrameWidth));}}}
    [DataSourceProperty] public float FrameHeight{get=>_height;set{if(Math.Abs(_height-value)>.001f){_height=value;OnPropertyChanged(nameof(FrameHeight));}}}
}
