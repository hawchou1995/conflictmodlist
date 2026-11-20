using CellMenu;
using TeamModVersions;
using TMPro;
using UnityEngine;
using SNetwork;

// Production code under a Unity/TMP boundary double. Actual interop method bodies are JIT-tested separately.
static void Check(bool v,string msg){if(!v)throw new Exception(msg);}
static string Plain(string text)=>System.Text.RegularExpressions.Regex.Replace(text,"<[^>]+>","");
var page=new CM_PageLoadout();var net=new TeamNetwork();var buttons=new LobbyButtons();var hud=new LobbyNetworkHud();var cam=new Camera();
var bars=page.m_playerLobbyBars;int[] order={2,0,3,1};
for(int i=0;i<4;i++){bars[i]=new(){m_playerSlotIndex=order[i]};bars[i].m_hasPlayerRoot.transform.localPosition=new(i*300,0,0);}
SNet.Slots.Players[2]=new(10,true,false);SNet.Slots.Players[0]=new(0,false,true);
void Frame(bool due=false){hud.Refresh(page,net,due);hud.Present(buttons,cam);}
TextMeshPro[] Visible()=>TextMeshPro.Clones.Where(x=>x.gameObject.activeInHierarchy).ToArray();
Frame();Check(Visible().Length==0,"equipment still hidden");
bars[0].m_weaponsHeaderText.enabled=true;Frame();Check(Visible().Length==0,"no provisional plate before the button row");
buttons.row=true;Frame();Check(Visible().Length==0,"missing template retry");
GuiManager.WatermarkLayer=new();var original=GuiManager.WatermarkLayer.m_watermark.m_watermarkText;original.text="Rundown watermark";
Frame();var me=Visible().Single();Check(me.rectTransform.parent==bars[0].m_hasPlayerRoot.transform && me.richText && Plain(me.text)=="01 /  local-0" && me.textInfo.characterCount>0,"native clone and indexed title under occupied pillar");
Check(FieldTerminalTheme.Opacity==0 && me.outlineWidth>0 && me.outlineColor==new Color32(12,12,12,255) && original.outlineWidth==0,"transparent plate, outlined clone, unchanged watermark");
Check(me.rectTransform.localPosition==new Vector3(120,42,1),"button top +2px in native pillar");
bars[1].m_weaponsHeaderText.enabled=true;Frame();var bot=Visible().Single(s=>Plain(s.text)=="02 /  BOT");Check(bot.rectTransform.localPosition==me.rectTransform.localPosition,"BOT and local share a bottom");
bars[1].m_hasPlayerRoot.transform.localPosition=new(355,0,0);Check(bot.rectTransform.TransformPoint(default).x==475,"inherits native motion without a HUD tick");
buttons.row=false;Frame();Frame();Check(me.rectTransform.localPosition==new Vector3(120,42,1),"temporary missing button must retain position");
SNet.Slots.Players[3]=new(99,false,false);bars[2].m_introDone=true;Frame();Check(Visible().Any(c=>Plain(c.text)=="03 /  peer99" && c.rectTransform.localPosition.x==1025),"cold Esc uses the native row");
buttons.row=true;net.version++;Frame(true);Check(Visible().Length==3 && Plain(me.text)=="01 /  local-1" && TextMeshPro.Clones.Count==3,"text refreshed in place");
SNet.Slots.Players[1]=new(101,false,false);bars[3].m_introDone=true;Frame();Check(Visible().Length==4,"four occupied slots");
SNet.Slots.Players[0]=null;Frame();Check(Visible().Length==3 && !bot.gameObject.activeSelf,"vacant slot immediately cleared");
hud.Clear();Check(Visible().Length==0 && original.text=="Rundown watermark" && original.gameObject.activeSelf,"page exit releases clones only");
Frame();Check(Visible().Length==3,"page reopen rebuilds once");hud.Clear();
Console.WriteLine("PASS native watermark clone, four occupied pillars, no generic inventory lookup, per-seat intro, 2px gap, motion, button loss, Esc fallback, vacancy and cleanup");
string[] samples={"本机 · P2P\n收 未知\n发 640 B/s\n采样 #12 · 0.42 秒前\n无共享真人", "队友\n游戏缓存·采样时间未知\n往返估值 44 ms\n波动/探针缺号 未知\n无近期兼容报告", "BOT · 网络\n无独立网络连接\n延迟 -- 抖动 -- 丢包 --", "整机网卡 · 非 P2P\n收 0 KiB/s\n发 1.2 KiB/s\n采样 #28 · 0.10 秒前\n游戏计数未确认"};
var cards=samples.Select((s,i)=>FieldTerminalTheme.Format(s,i,i==2)).ToArray();
Check(Plain(cards[0]).Contains("RX  收   未知\nTX  发   640 B/s") && Plain(cards[3]).Contains("非 P2P\nRX  收   0 KiB/s"),"unknown, idle and host scope survive styling");
Check(!cards[2].Contains($"#{FieldTerminalTheme.Accent:X6}") && !FieldTerminalTheme.Format("<sprite=0>",0,false).Contains("<sprite"),"neutral BOT and no source markup");
if(args is ["--preview",var file]) File.WriteAllText(file,System.Text.Json.JsonSerializer.Serialize(new{cards,FieldTerminalTheme.Base,FieldTerminalTheme.Accent,FieldTerminalTheme.Quiet,FieldTerminalTheme.Ink,FieldTerminalTheme.Opacity,FieldTerminalTheme.OutlineGray,FieldTerminalTheme.OutlineWidth,FieldTerminalTheme.Pad,FieldTerminalTheme.Type,FieldTerminalTheme.TabWidth,FieldTerminalTheme.RuleHeight}));
Console.WriteLine("PASS theme: source and direction preserved; unknown distinct from zero; no tag injection");

namespace TeamModVersions {
 internal class TeamNetwork {public int version;public string SlotText(ulong id,bool local,bool bot)=>bot?"BOT":local?"local-"+version:"peer"+id;}
 internal class LobbyButtons {public bool row;public bool CompareRect(int i,CM_PlayerLobbyBar b,bool bot,out Transform? owner,out Rect r,out float z){owner=b.m_hasPlayerRoot.transform;r=new(120,20,80,20);z=1;return row;}}
 internal static class Plugin {public static Log Logger=new();} internal class Log {public void LogInfo(string s){} public void LogWarning(string s)=>throw new Exception(s);}
}
namespace CellMenu {internal class CM_PageLoadout {public CM_PlayerLobbyBar[] m_playerLobbyBars=new CM_PlayerLobbyBar[4];} internal class CM_PlayerLobbyBar { public GameObject m_hasPlayerRoot=new();public int m_playerSlotIndex;public P? m_player=>null;public bool m_introDone;public TextMeshPro m_weaponsHeaderText=new(){enabled=false};public object m_inventorySlotItems=>throw new MissingMethodException("Void System.__Canon..ctor(IntPtr)");}}
namespace SNetwork {internal record P(ulong Lookup,bool IsLocal,bool IsBot);internal class Slots {public P?[] Players=new P?[4];public P? GetPlayerInSlot(int n)=>Players[n];} internal static class SNet{public static Slots Slots=new();}}
internal static class GuiManager{public static Layer? WatermarkLayer;}internal class Layer {public Watermark m_watermark=new();}internal class Watermark {public TextMeshPro m_watermarkText=new();}
namespace TMPro {
 internal enum FontStyles {UpperCase=1} internal enum TextAlignmentOptions{BottomLeft}internal enum TextOverflowModes{Overflow}
 internal class TextMeshPro:UnityEngine.Object {
  public static readonly List<TextMeshPro> Clones=new();public static Transform? Destination;
  public TextMeshPro(){}public TextMeshPro(IntPtr ptr){rectTransform.SetParent(Destination!,false);Clones.Add(this);}
  public GameObject gameObject=new();public RectTransform rectTransform=>gameObject.transform;public Renderer renderer=new();public bool enabled=true,enableAutoSizing,richText,enableWordWrapping;public bool isActiveAndEnabled=>enabled&&gameObject.activeInHierarchy;
  public string text="";public float fontSize;public FontStyles fontStyle;public TextAlignmentOptions alignment;public TextOverflowModes overflowMode;public Color color=Color.white;public Vector4 margin;
  public Color32 outlineColor;public float outlineWidth;
  public (int characterCount,int unused) textInfo=>(text.Length,0);public Vector2 GetPreferredValues(string s)=>new(s.Length*9,20);public void ForceMeshUpdate(bool unused,bool unused2){}
 }
}
namespace UnityEngine {
 internal record struct Color32(byte r,byte g,byte b,byte a);
 internal class Object {public IntPtr Pointer=>(IntPtr)1;public static Object Instantiate(Object original,Transform? parent,bool keepWorld){if(original!=GuiManager.WatermarkLayer?.m_watermark.m_watermarkText || keepWorld || parent==null)throw new Exception("wrong prefab/parent");TextMeshPro.Destination=parent;return new Object();}public static void Destroy(Object obj){if(obj is GameObject g)g.SetActive(false);}}
 internal class GameObject:Object {public GameObject(string name=""){this.name=name;transform=new(this);}public string name;public int layer;public RectTransform transform;public bool activeSelf=true;public bool activeInHierarchy=>activeSelf && (transform.parent?.gameObject.activeInHierarchy??true);public void SetActive(bool v)=>activeSelf=v;public T AddComponent<T>() where T:new()=>new T();}
 internal class Transform(GameObject obj){public GameObject gameObject=>obj;public Transform? parent;public Vector3 localScale=new(1,1,1),localPosition;public void SetParent(Transform p,bool flag)=>parent=p;public Vector3 TransformPoint(Vector3 p)=>parent?.TransformPoint(p+localPosition)??(p+localPosition);public Vector3 InverseTransformPoint(Vector3 p)=>p-TransformPoint(default);}
 internal class RectTransform(GameObject obj):Transform(obj){public Vector2 anchorMin,anchorMax,pivot=new(.5f,.5f),sizeDelta=new(200,100);public Rect rect=>new(0,0,sizeDelta.x,sizeDelta.y);}
 internal class Camera{public Vector3 WorldToScreenPoint(Vector3 p)=>p;}
 internal class Renderer {public bool enabled=true;public int sortingOrder,sortingLayerID;}
 internal class SpriteRenderer:Renderer {public Sprite? sprite;public Color color;}
 internal enum SpriteMeshType{FullRect} internal class Sprite:Object {public static Sprite Create(object t,Rect r,Vector2 p,int u,int b,SpriteMeshType m)=>new();}internal class Texture2D {public static object whiteTexture=new();}
 internal record struct Vector2(float x,float y){public static Vector2 zero=>default;}internal record struct Vector4(float x,float y,float z,float w){public static Vector4 zero=>default;}
 internal record struct Vector3(float x,float y,float z){public static Vector3 up=>new(0,1,0);public static Vector3 operator+(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);public static Vector3 operator-(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);}
 internal record struct Rect(float x,float y,float width,float height){public float xMin=>x;public float yMax=>y+height;}internal record struct Color(float r,float g,float b,float a){public static Color white=>new(1,1,1,1);}
}
