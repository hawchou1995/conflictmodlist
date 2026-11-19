using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using FontInteropGate;

// 大厅接线闸门：直接检查生产 conflictmodlist.dll 的 IL 调用点，证明
//   1) 开表 / 关闭都真的挂到大厅 GuiLayer 上（m_guiLayer + AddRectComp，且模板是 GameObject 那一版重载），
//      并用大厅原生 CM_Item 的 OnBtnPressCallback 分发；
//   2) 关闭落点走真实菜单相机投影（WorldSpace / 取不到 Canvas 也不禁用），命中盒按实测比例设置；
//   3) 表头名字取大厅原生 TMP 已排版字符并按段绘制（richText 关闭）；
//   4) PlaceClose 与 OnGUI 共用 PanelAnchor.PanelShown 可见性判定（隐藏态不得留命中区）；
//   5) 没有退化成全局原始输入或抢系统光标。
// 能力边界：只证明“编译产物里确实调用了这些 API”，不证明大厅真的点到、位置真的对齐；那部分只能实机验证。
// 用法: LobbyWiringGate <plugin.dll> [--calls <子串>] [--sig <方法名>]

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: LobbyWiringGate <plugin.dll> [--calls <substring>] [--sig <memberName>]");
    return 2;
}

string pluginPath = Path.GetFullPath(args[0]);
if (!File.Exists(pluginPath)) { Console.Error.WriteLine($"missing {pluginPath}"); return 2; }
Console.WriteLine($"plugin : {pluginPath}");
Console.WriteLine($"sha256 : {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pluginPath)))}");

if (args.Length >= 3 && args[1] == "--sig")
{
    foreach (var signature in SignaturesOf(pluginPath, args[2]).OrderBy(s => s, StringComparer.Ordinal))
        Console.WriteLine($"{args[2]}{signature}");
    return 0;
}

var calls = new List<(string Caller, string Callee)>();
var methods = new HashSet<string>(StringComparer.Ordinal);
try
{
    using var stream = File.OpenRead(pluginPath);
    using var pe = new PEReader(stream);
    var md = pe.GetMetadataReader();
    foreach (var typeHandle in md.TypeDefinitions)
    {
        var type = md.GetTypeDefinition(typeHandle);
        string typeName = FullName(md, type);
        foreach (var methodHandle in type.GetMethods())
        {
            var method = md.GetMethodDefinition(methodHandle);
            string label = $"{typeName}.{md.GetString(method.Name)}";
            methods.Add(label);
            if (method.RelativeVirtualAddress == 0) continue;
            foreach (var token in IlCalls.CallTokens(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? Array.Empty<byte>()))
                calls.Add((label, Describe(md, token)));
        }
    }
}
catch (IlCalls.DecodeException e)
{
    Console.WriteLine($"FAIL IL 解码失败，不能少扫调用点：{e.Message}");
    return 1;
}
catch (Exception e)
{
    Console.WriteLine($"FAIL 读取失败：{e.GetType().Name}: {e.Message}");
    return 1;
}

if (args.Length >= 3 && args[1] == "--calls")
{
    foreach (var call in calls.Where(c => c.Caller.Contains(args[2], StringComparison.Ordinal)).OrderBy(c => c.Caller, StringComparer.Ordinal).ThenBy(c => c.Callee, StringComparer.Ordinal))
        Console.WriteLine($"{call.Caller} -> {call.Callee}");
    return 0;
}

const string Driver = "TeamModVersions.TeamVersionsDriver.";
var required = new (string Caller, string Callee)[]
{
    // 开表：大厅 GuiLayer 上挂原生 CM_Item，模板 = 玩家栏自带的邀请按钮
    (Driver + "PlaceButton", "CellMenu.CM_PageBase::get_m_guiLayer"),
    (Driver + "PlaceButton", "GuiLayer::AddRectComp"),
    (Driver + "PlaceButton", "CellMenu.CM_PlayerLobbyBar::get_m_inviteButton"),
    (Driver + "PlaceButton", "CellMenu.CM_PlayerLobbyBar::get_m_hasPlayerRoot"),
    (Driver + "PlaceButton", "CellMenu.CM_PlayerLobbyBar::get_m_player"),
    (Driver + "PlaceButton", "CellMenu.CM_Item::set_ID"),
    (Driver + "PlaceButton", "CellMenu.CM_Item::set_OnBtnPressCallback"),
    (Driver + "PlaceButton", "Il2CppInterop.Runtime.DelegateSupport::ConvertDelegate"),
    // 关闭：同样挂在该 GuiLayer 上，模板 = 自己的开表按钮（GameObject）
    (Driver + "CreateClose", "CellMenu.CM_PageBase::get_m_guiLayer"),
    (Driver + "CreateClose", "GuiLayer::AddRectComp"),
    (Driver + "CreateClose", "UnityEngine.Component::get_gameObject"),
    (Driver + "CreateClose", "CellMenu.CM_Item::set_ID"),
    (Driver + "CreateClose", "CellMenu.CM_Item::set_OnBtnPressCallback"),
    (Driver + "CreateClose", "Il2CppInterop.Runtime.DelegateSupport::ConvertDelegate"),
    (Driver + "CreateClose", "TeamModVersions.TeamVersionsDriver::HideVisuals"),
    (Driver + "HideVisuals", "UnityEngine.GameObject::GetComponentsInChildren"),
    (Driver + "HideVisuals", "UnityEngine.Renderer::set_enabled"),
    (Driver + "HideVisuals", "UnityEngine.Behaviour::set_enabled"),
    // 关闭落点：真实菜单相机 + 屏幕点到父平面（WorldSpace / 取不到 Canvas 也走这条路，不因 RenderMode 禁用）
    (Driver + "MenuCamera", "CellMenu.CM_PageBase::get_Camera"),
    (Driver + "MenuCamera", "UnityEngine.Canvas::get_worldCamera"),
    (Driver + "MenuCamera", "UnityEngine.Camera::get_main"),
    (Driver + "TryProject", "UnityEngine.RectTransformUtility::ScreenPointToWorldPointInRectangle"),
    (Driver + "TryProject", "UnityEngine.Transform::InverseTransformVector"),
    (Driver + "TryProject", "TeamModVersions.PanelAnchor::LocalPerPixel"),
    (Driver + "PlaceAt", "UnityEngine.Canvas::get_renderMode"),
    (Driver + "PlaceAt", "UnityEngine.Canvas::get_scaleFactor"),
    (Driver + "PlaceAt", "TeamModVersions.PanelAnchor::CanvasOffset"),
    (Driver + "PlaceAt", "TeamModVersions.PanelAnchor::Center"),
    (Driver + "PlaceAt", "UnityEngine.Transform::set_position"),
    (Driver + "ApplyHitBox", "CellMenu.CM_Item::get_m_collider"),
    (Driver + "ApplyHitBox", "UnityEngine.BoxCollider2D::set_size"),
    (Driver + "ApplyHitBox", "TeamModVersions.PanelAnchor::BoxSize"),
    (Driver + "ApplyHitBox", "TeamModVersions.PanelAnchor::IsSane"),
    // 可见性：PlaceClose 与 OnGUI 必须共用同一条判定（隐藏态不留命中区）
    (Driver + "PanelVisible", "TeamModVersions.PanelAnchor::PanelShown"),
    (Driver + "PlaceClose", "TeamModVersions.TeamVersionsDriver::PanelVisible"),
    (Driver + "OnGUI", "TeamModVersions.TeamVersionsDriver::PanelVisible"),
    // 表头名字/颜色：按 ID 取大厅原生名字控件（TMP 已排版字符优先）
    (Driver + "ResolveNames", "CellMenu.CM_PageLoadout::get_m_playerLobbyBars"),
    (Driver + "ResolveNames", "CellMenu.CM_PlayerLobbyBar::get_m_nickText"),
    (Driver + "ResolveNames", "CellMenu.CM_PlayerLobbyBar::get_m_player"),
    (Driver + "ResolveNames", "SNetwork.SNet_Player::get_IsLocal"),
    (Driver + "ResolveNames", "SNetwork.SNet_Player::get_Lookup"),
    (Driver + "ResolveNames", "TeamModVersions.NameMarkup::Resolve"),
    (Driver + "ReadNativeName", "TMPro.TMP_Text::get_text"),
    (Driver + "ReadNativeName", "TMPro.TMP_Text::get_textInfo"),
    (Driver + "ReadNativeName", "TMPro.TMP_Text::GetParsedText"),
    (Driver + "ReadNativeName", "TMPro.TMP_TextInfo::get_characterInfo"),
    (Driver + "ReadNativeName", "TMPro.TMP_CharacterInfo::get_index"),
    (Driver + "ReadNativeName", "TMPro.TMP_CharacterInfo::get_stringLength"),
    (Driver + "ReadNativeName", "TMPro.TMP_CharacterInfo::get_color"),
    (Driver + "ReadNativeName", "TeamModVersions.NameMarkup::FromRendered"),
    (Driver + "ReadNativeName", "TeamModVersions.NameMarkup::Parse"),
    (Driver + "DrawRuns", "UnityEngine.GUI::Label"),
    (Driver + "DrawRuns", "UnityEngine.GUIStyle::CalcSize"),
    (Driver + "Update", "TeamModVersions.TeamVersionsDriver::ResolveNames"),
    (Driver + "Update", "TeamModVersions.PanelModel::Create"),
    (Driver + "HandleKeys", "UnityEngine.Event::get_keyCode"),
    (Driver + "HandleKeys", "UnityEngine.Event::Use"),
    (Driver + "HandleKeys", "TeamModVersions.PanelModel::Scroll"),
    (Driver + "HandleKeys", "TeamModVersions.PanelModel::MaxScroll"),
    (Driver + "Table", "UnityEngine.GUI::BeginScrollView"),
    (Driver + "Table", "TeamModVersions.PanelModel::FirstVisible"),
    (Driver + "Table", "TeamModVersions.NameMarkup::Resolve"),
};
var requiredMethods = new[]
{
    Driver + "CloseFromPress", Driver + "Close", Driver + "RemoveClose",
    Driver + "ReadNativeName", Driver + "ResolveNames", Driver + "DrawRuns",
    Driver + "PanelVisible", Driver + "PlaceButton", Driver + "CreateClose",
};
var forbidden = new[]
{
    "UnityEngine.Input::GetMouseButtonDown", "UnityEngine.Input::GetMouseButtonUp", "UnityEngine.Input::GetMouseButton",
    "UnityEngine.Input::get_mousePosition", "UnityEngine.Input::GetKeyDown", "UnityEngine.Input::GetKeyUp", "UnityEngine.Input::GetKey",
    "UnityEngine.Input::GetAxis", "UnityEngine.Cursor::set_visible", "UnityEngine.Cursor::set_lockState",
    "UnityEngine.Screen::SetResolution",
    // 注：表头名字不开 richText —— 校验方式见 offline tests（只产出“文字+颜色”分段）与
    // DrawRuns 的 GUIStyle::CalcSize/GUI::Label 逐段绘制接线；这里不按 IL 常量判 richText 取值。
};

int failures = 0;

Console.WriteLine();
Console.WriteLine("== 必须存在的原生按压 / 清单 / 可见性接线 ==");
foreach (var (caller, callee) in required)
{
    bool ok = calls.Any(c => c.Caller == caller && c.Callee == callee);
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {caller} -> {callee}");
    if (!ok) failures++;
}
foreach (var name in requiredMethods)
{
    bool ok = methods.Contains(name);
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} 方法存在 {name}");
    if (!ok) failures++;
}

Console.WriteLine();
Console.WriteLine("== 必须钉住的重载：GuiLayer.AddRectComp 只能用 GameObject 模板那一版 ==");
const string RectCompSignature = "(UnityEngine.GameObject, GuiAnchor, UnityEngine.Vector2, UnityEngine.Transform)";
var rectCompSignatures = SignaturesOf(pluginPath, "AddRectComp");
foreach (var signature in rectCompSignatures.OrderBy(s => s, StringComparer.Ordinal))
    Console.WriteLine($"   AddRectComp{signature}");
bool rectCompOk = rectCompSignatures.Count == 1 && rectCompSignatures.Contains(RectCompSignature);
Console.WriteLine($"{(rectCompOk ? "PASS" : "FAIL")} 唯一重载 = {RectCompSignature}（不接受预制体路径字符串那一版）");
if (!rectCompOk) failures++;

Console.WriteLine();
Console.WriteLine("== 必须不存在的全局原始输入 / 光标抢占 ==");
foreach (var target in forbidden)
{
    int hits = calls.Count(c => c.Callee == target);
    Console.WriteLine($"{(hits == 0 ? "PASS" : "FAIL")} {target}：{hits} 处");
    if (hits != 0) failures++;
}

Console.WriteLine();
Console.WriteLine($"summary: calls={calls.Count} required={required.Length + requiredMethods.Length + 1} forbidden={forbidden.Length} failures={failures} -> {(failures == 0 ? "GREEN" : "RED")}");
return failures == 0 ? 0 : 1;

// ---------------------------------------------------------------- helpers

static string FullName(MetadataReader md, TypeDefinition type)
{
    string ns = md.GetString(type.Namespace), name = md.GetString(type.Name);
    return ns.Length == 0 ? name : ns + "." + name;
}

static string TypeRefName(MetadataReader md, TypeReferenceHandle handle)
{
    var tr = md.GetTypeReference(handle);
    string ns = md.GetString(tr.Namespace), name = md.GetString(tr.Name);
    return ns.Length == 0 ? name : ns + "." + name;
}

static string Describe(MetadataReader md, int token)
{
    var handle = MetadataTokens.EntityHandle(token);
    switch (handle.Kind)
    {
        case HandleKind.MemberReference:
        {
            var mr = md.GetMemberReference((MemberReferenceHandle)handle);
            string parent = mr.Parent.Kind switch
            {
                HandleKind.TypeReference => TypeRefName(md, (TypeReferenceHandle)mr.Parent),
                HandleKind.TypeDefinition => FullName(md, md.GetTypeDefinition((TypeDefinitionHandle)mr.Parent)),
                _ => "<typespec>",
            };
            return parent + "::" + md.GetString(mr.Name);
        }
        case HandleKind.MethodDefinition:
        {
            var m = md.GetMethodDefinition((MethodDefinitionHandle)handle);
            return FullName(md, md.GetTypeDefinition(m.GetDeclaringType())) + "::" + md.GetString(m.Name);
        }
        case HandleKind.MethodSpecification:
        {
            var spec = md.GetMethodSpecification((MethodSpecificationHandle)handle);
            return Describe(md, MetadataTokens.GetToken(spec.Method));
        }
        default:
            return "<" + handle.Kind + ">";
    }
}

// 某个方法名在 DLL 里出现过的全部签名（参数表），用来钉住重载（如 AddRectComp 的 GameObject 版 vs 预制体路径版）。
static List<string> SignaturesOf(string pluginPath, string memberName)
{
    var found = new List<string>();
    using var stream = File.OpenRead(pluginPath);
    using var pe = new PEReader(stream);
    var md = pe.GetMetadataReader();
    var provider = new SignatureFormat();
    foreach (var handle in md.MemberReferences)
    {
        var mr = md.GetMemberReference(handle);
        if (!md.GetString(mr.Name).Equals(memberName, StringComparison.Ordinal)) continue;
        if (mr.GetKind() != MemberReferenceKind.Method) continue;
        var signature = mr.DecodeMethodSignature(provider, null);
        string text = "(" + string.Join(", ", signature.ParameterTypes) + ")";
        if (!found.Contains(text)) found.Add(text);
    }
    return found;
}

internal sealed class SignatureFormat : ISignatureTypeProvider<string, object?>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeDefinition(handle);
        string ns = reader.GetString(type.Namespace), name = reader.GetString(type.Name);
        return ns.Length == 0 ? name : ns + "." + name;
    }
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeReference(handle);
        string ns = reader.GetString(type.Namespace), name = reader.GetString(type.Name);
        return ns.Length == 0 ? name : ns + "." + name;
    }
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => "<typespec>";
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
    public string GetByReferenceType(string elementType) => "ref " + elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
}
