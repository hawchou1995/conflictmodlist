using CellMenu;
using SNetwork;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TeamModVersions;

internal sealed class LobbyNetworkHud
{
    private sealed class Seat
    {
        internal CM_PlayerLobbyBar? Bar;
        internal Transform? Root;
        internal ulong Id;
        internal bool Bot,Local,Ready,Warned,HadChip;
        internal string? Text,Rendered,State;
        internal TextMeshPro? Label;
        internal Transform? Plate;
        internal void Reset()
        {
            if(Label!=null){Label.gameObject.SetActive(false); Object.Destroy(Label.gameObject);}
            Label=null; Plate=null; Root=null; Bar=null; Text=Rendered=State=null; Ready=Warned=HadChip=false;
        }
    }
    private readonly Seat[] seats={new(),new(),new(),new()};
    private Sprite? square;
    internal void Clear(){foreach(var s in seats)s.Reset(); if(square!=null)Object.Destroy(square); square=null;}

    // No access to m_inventorySlotItems: its interop dictionary is not a managed Dictionary.
    // The native equipment heading enters with the column; a finished bar also covers cold Esc.
    internal bool Refresh(CM_PageLoadout page,TeamNetwork network,bool dataDue)
    {
        var bars=page.m_playerLobbyBars; bool dirty=false;
        for(int i=0;i<seats.Length;i++)
        {
            var s=seats[i];
            try
            {
                var bar=bars!=null && i<bars.Length ? bars[i] : null;
                var root=bar?.m_hasPlayerRoot;
                if(bar==null || root==null || !root.activeInHierarchy){dirty|=s.Bar!=null; s.Reset();continue;}
                int slot=bar.m_playerSlotIndex; var slots=SNet.Slots;
                var p=slots!=null && slot>=0 && slot<seats.Length ? slots.GetPlayerInSlot(slot) : bar.m_player;
                if(p==null){dirty|=s.Bar!=null; s.Reset();continue;}
                bool bot=p.IsBot, local=!bot && p.IsLocal; ulong id=bot?0:p.Lookup;
                if(s.Bar!=bar || s.Root!=root.transform || s.Id!=id || s.Bot!=bot || s.Local!=local)
                {s.Reset();s.Bar=bar;s.Root=root.transform;s.Id=id;s.Bot=bot;s.Local=local;dirty=true;}
                var header=bar.m_weaponsHeaderText;
                bool ready=bar.m_introDone || (header!=null && header.isActiveAndEnabled && header.renderer.enabled && header.color.a>0);
                dirty|=s.Ready!=ready; s.Ready=ready;
                if(dataDue || s.Text==null)s.Text=network.SlotText(id,local,bot);
            }
            catch(Exception e){s.Ready=false; Warn(s,i,"read",e);}
        }
        return dirty;
    }
    private static void Warn(Seat s,int i,string stage,Exception e)
    {if(!s.Warned)Plugin.Logger.LogWarning($"[槽位网络] 第{i+1}栏 {stage}: {e}");s.Warned=true;}
    private static void State(Seat s,int i,string state)
    {if(s.State!=state){Plugin.Logger.LogInfo($"[槽位网络] 第{i+1}栏 {state}");s.State=state;}}

    private static Color Tint(int rgb,float alpha=1f) => new(((rgb>>16)&255)/255f,((rgb>>8)&255)/255f,(rgb&255)/255f,alpha);

    // Parent to the scaled unit plate so the header rule resizes without new objects or a per-frame layout pass.
    private static void Rule(SpriteRenderer plate,Transform parent,Sprite sprite,int layer,bool tab,bool bot)
    {
        float w=tab?FieldTerminalTheme.TabWidth:1, h=FieldTerminalTheme.RuleHeight;
        var go=new GameObject(tab?"ChannelTab":"HeaderRule"); go.layer=layer; var t=go.transform;t.SetParent(parent,false);
        t.localPosition=new Vector3((w-1)/2,(1-h)/2,0); t.localScale=new Vector3(w,h,1);
        var r=go.AddComponent<SpriteRenderer>();r.sprite=sprite;r.color=Tint(tab&&!bot?FieldTerminalTheme.Accent:FieldTerminalTheme.Quiet,tab?1f:.4f);
        r.sortingLayerID=plate.sortingLayerID;r.sortingOrder=plate.sortingOrder+(tab?2:1);
    }

    // NetworkQualityTracker@056529a: clone the actual watermark TMP under the occupied bar, not
    // a projected imitation. Use the non-generic native Instantiate overload and a concrete wrapper.
    private void Attach(Seat s,TextMeshPro template,int i)
    {
        var instance=Object.Instantiate((Object)template,s.Root,false);
        var label=s.Label=new TextMeshPro(instance.Pointer);
        try
        {
            label.gameObject.SetActive(false); label.gameObject.name=$"ConflictModList_Network_{i}";
            label.enabled=true; label.renderer.enabled=true;
            label.fontStyle &= ~FontStyles.UpperCase; label.enableAutoSizing=false; label.fontSize=FieldTerminalTheme.Type;
            label.richText=true; label.enableWordWrapping=false; label.overflowMode=TextOverflowModes.Overflow;
            label.color=Color.white; label.margin=Vector4.zero; label.alignment=TextAlignmentOptions.BottomLeft;
            // Use TMP's per-text outline, not the watermark's shared font material.
            const byte edge=FieldTerminalTheme.OutlineGray;
            label.outlineColor=new Color32(edge,edge,edge,255); label.outlineWidth=FieldTerminalTheme.OutlineWidth;
            var rt=label.rectTransform; rt.anchorMin=rt.anchorMax=new Vector2(.5f,.5f); rt.pivot=Vector2.zero;
            var go=new GameObject("NetworkPlate"); go.transform.SetParent(rt,false); go.layer=label.gameObject.layer;
            var bg=go.AddComponent<SpriteRenderer>();
            if(square==null)square=Sprite.Create(Texture2D.whiteTexture,new Rect(0,0,1,1),new Vector2(.5f,.5f),1,0,SpriteMeshType.FullRect);
            bg.sprite=square; bg.color=Tint(FieldTerminalTheme.Base,FieldTerminalTheme.Opacity);
            bg.sortingLayerID=label.renderer.sortingLayerID; bg.sortingOrder=label.renderer.sortingOrder;
            label.renderer.sortingOrder=bg.sortingOrder+3; s.Plate=go.transform;
            Rule(bg,s.Plate,square,go.layer,false,s.Bot); Rule(bg,s.Plate,square,go.layer,true,s.Bot);
        }
        catch {label.gameObject.SetActive(false); Object.Destroy(label.gameObject);s.Label=null;s.Plate=null;throw;}
    }

    internal void Present(LobbyButtons buttons,Camera? camera)
    {
        for(int i=0;i<seats.Length;i++)
        {
            var s=seats[i]; if(s.Bar==null || s.Root==null)continue;
            try
            {
                if(!s.Ready || s.Text==null){if(s.Label!=null && s.Label.gameObject.activeSelf)s.Label.gameObject.SetActive(false);State(s,i,"waiting-equipment");continue;}
                var template=GuiManager.WatermarkLayer?.m_watermark?.m_watermarkText;
                if(s.Label==null && template==null){State(s,i,"waiting-watermark");continue;}
                bool chip=buttons.CompareRect(i,s.Bar,s.Bot,out var owner,out var bounds,out var z) && owner!=null;
                // Do not paint a provisional fallback during the intro. If the fully opened page has no
                // chip at all, use the reference mod's native row; losing a button must not lose the HUD.
                if(!chip && !s.Bar.m_introDone && s.Label==null){State(s,i,"waiting-button-row");continue;}
                if(s.Label==null)Attach(s,template!,i);
                var label=s.Label!; var rt=label.rectTransform;
                if(s.Rendered!=s.Text)
                {
                    var styled=FieldTerminalTheme.Format(s.Text,i,s.Bot); label.text=styled;
                    var size=label.GetPreferredValues(styled); const float pad=FieldTerminalTheme.Pad;
                    rt.sizeDelta=new Vector2(Math.Max(1,size.x)+2*pad,Math.Max(1,size.y)+2*pad);
                    label.margin=new Vector4(pad,pad,pad,pad);
                    var plate=s.Plate!; var r=rt.sizeDelta; plate.localScale=new Vector3(r.x,r.y,1);plate.localPosition=new Vector3(r.x/2,r.y/2,0);
                    s.Rendered=s.Text;
                }
                if(chip)
                {
                    var world=owner!.TransformPoint(new Vector3(bounds.xMin,bounds.yMax,z));
                    var p=s.Root.InverseTransformPoint(world); float gap=2;
                    if(camera!=null){var v=camera.WorldToScreenPoint(world);var u=camera.WorldToScreenPoint(s.Root.TransformPoint(p+Vector3.up));float dy=u.y-v.y;if(v.z>0 && float.IsFinite(dy) && dy>.001f)gap=2/dy;}
                    rt.localPosition=new Vector3(p.x,p.y+gap,p.z);
                    s.HadChip=true;
                }
                else if(!s.HadChip && template!=null) rt.localPosition=HikariaLoadoutAnchor.Fallback(template,rt);
                if(!label.gameObject.activeSelf) label.gameObject.SetActive(true);
                var state=chip?"native-tmp/compare-row":s.HadChip?"native-tmp/retained-row":"native-tmp/reference-row";
                if(s.State!=state)
                {
                    label.ForceMeshUpdate(false,false);
                    Plugin.Logger.LogDebug($"[槽位网络] 第{i+1}栏 {state} glyphs={label.textInfo.characterCount} active={label.gameObject.activeInHierarchy}");
                    s.State=state;
                }
            }
            catch(Exception e){Warn(s,i,"native-tmp",e);}
        }
    }
}
