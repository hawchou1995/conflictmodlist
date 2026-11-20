using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using FontInteropGate;

// 从编译产物检查常显网络 HUD、逐玩家模组入口、同源轨道/命中/光标和原生输入。
// 正向检查再配合禁止网络主动探测/重复消费输入的断言，不用生产方法名的存在代替接线。
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

var calls = new List<(string Caller, int Offset, string Callee)>();
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
            foreach (var site in IlCalls.CallSites(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? Array.Empty<byte>()))
                calls.Add((label, site.Offset, Describe(md, site.Token)));
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
    (Driver + "PlaceButton", "TeamModVersions.LobbyButtons::Update"),
    ("TeamModVersions.LobbyButtons.Update", "TeamModVersions.LobbyButtons::Reconcile"),
    ("TeamModVersions.LobbyButtons.Reconcile", "CellMenu.CM_PlayerLobbyBar::get_m_clothesButton"),
    ("TeamModVersions.LobbyButtons.Player", "SNetwork.SNet_Player::get_IsBot"),
    ("TeamModVersions.LobbyButtons.Build", "GuiLayer::AddRectComp"),
    ("TeamModVersions.LobbyButtons.Build", "CellMenu.CM_Item::set_OnBtnPressCallback"),
    ("TeamModVersions.LobbyButtons.Build", "Il2CppInterop.Runtime.DelegateSupport::ConvertDelegate"),
    (Driver + "LateUpdate", "TeamModVersions.LobbyNetworkHud::Present"),
    (Driver + "LateUpdate", "TeamModVersions.LobbyNetworkHud::Refresh"),
    (Driver + "LateUpdate", "TeamModVersions.TeamVersionsDriver::PlaceButton"),
    ("TeamModVersions.LobbyNetworkHud.Refresh", "CellMenu.CM_PlayerLobbyBar::get_m_hasPlayerRoot"),
    ("TeamModVersions.LobbyNetworkHud.Attach", "UnityEngine.Object::Instantiate"),
    ("TeamModVersions.LobbyNetworkHud.Refresh", "CellMenu.CM_PageLoadout::get_m_playerLobbyBars"),
    ("TeamModVersions.LobbyNetworkHud.Refresh", "SNetwork.SNet_PlayerSlotManager::GetPlayerInSlot"),
    ("TeamModVersions.LobbyNetworkHud.Refresh", "SNetwork.SNet_Player::get_IsBot"),
    ("TeamModVersions.LobbyNetworkHud.Refresh", "TeamModVersions.TeamNetwork::SlotText"),
    ("TeamModVersions.LobbyNetworkHud.Present", "TeamModVersions.LobbyButtons::CompareRect"),
    ("TeamModVersions.LobbyNetworkHud.Present", "PUI_Watermark::get_m_watermarkText"),
    ("TeamModVersions.LobbyNetworkHud.Present", "TeamModVersions.LobbyNetworkHud::Attach"),
    ("TeamModVersions.LobbyNetworkHud.Attach", "TMPro.TextMeshPro::.ctor"),
    (Driver + "Update", "TeamModVersions.TeamNetwork::Tick"),
    (Driver + "OnGUI", "TeamModVersions.TeamVersionsDriver::DrawPanelCursor"),
    (Driver + "Table", "TeamModVersions.TeamVersionsDriver::DrawRail"),
    (Driver + "HandleMouse", "TeamModVersions.RailDrag::Sample"),
    (Driver + "ResetInput", "TeamModVersions.RailDrag::Reset"),
    // 关闭：旧透明原生命中区已移除；关闭只由 HandleMouse 的“大厅光标 + 主按键边沿”决定
    (Driver + "HandleMouse", "TeamModVersions.PressOwnership::Arm"),
    (Driver + "HandleMouse", "TeamModVersions.TeamVersionsDriver::Close"),
    (Driver + "HandleMouse", "TeamModVersions.WheelRouter::Suspend"),
    (Driver + "TryButton", "TeamModVersions.TeamVersionsDriver::ReadPhysicalButton"),
    (Driver + "TryButton", "CellMenu.CM_PageBase::get_MainButtonStatus"),
    (Driver + "ReadPhysicalButton", "UnityEngine.Input::GetMouseButton"),
    // 关表那一次按压的归属：判定在驱动侧，前缀只跳过大厅自己的按压分发
    (Driver + "SuppressLobbyPress", "TeamModVersions.PressOwnership::ShouldSuppress"),
    (Driver + "SuppressLobbyPress", "TeamModVersions.TeamVersionsDriver::SamePage"),
    // 可见性：OnGUI 与按压 / 滚轮命中共用同一条判定（隐藏态不留命中区）
    (Driver + "PanelVisible", "TeamModVersions.PanelAnchor::PanelShown"),
    (Driver + "OnGUI", "TeamModVersions.TeamVersionsDriver::PanelVisible"),
    // 换页 / 卸载必须显式关表（重建入口不再顺带关表）
    (Driver + "SetPage", "TeamModVersions.TeamVersionsDriver::Close"),
    (Driver + "Shutdown", "TeamModVersions.TeamVersionsDriver::Close"),
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
    // 清单：BeginGroup 裁切（不再用 BeginScrollView 自带的 IMGUI 滚轮），滚动量来自 Update 里的滚动状态
    (Driver + "Table", "UnityEngine.GUI::BeginGroup"),
    (Driver + "Table", "UnityEngine.GUI::EndGroup"),
    (Driver + "Table", "TeamModVersions.PanelModel::FirstVisible"),
    (Driver + "Table", "TeamModVersions.NameMarkup::Resolve"),
    (Driver + "Table", "TeamModVersions.TeamVersionsDriver::CloseLocalRect"),
    (Driver + "Table", "TeamModVersions.PanelHit::Inside"),
    // 实体鼠标：大厅页自己的光标 / 主按键 / 滚轮（同一事件源）；渲染矩形与命中矩形同源
    (Driver + "Update", "TeamModVersions.TeamVersionsDriver::HandleMouse"),
    (Driver + "HandleMouse", "TeamModVersions.TeamVersionsDriver::PanelVisible"),
    (Driver + "HandleMouse", "TeamModVersions.TeamVersionsDriver::SampleCursor"),
    (Driver + "HandleMouse", "TeamModVersions.TeamVersionsDriver::CloseButtonRect"),
    (Driver + "HandleMouse", "TeamModVersions.TeamVersionsDriver::ListScreenRect"),
    (Driver + "HandleMouse", "TeamModVersions.TeamVersionsDriver::TryButton"),
    (Driver + "HandleMouse", "TeamModVersions.ClosePressGate::Sample"),
    (Driver + "HandleMouse", "TeamModVersions.PanelHit::Inside"),
    (Driver + "HandleMouse", "TeamModVersions.PanelModel::Scroll"),
    (Driver + "HandleMouse", "TeamModVersions.TeamVersionsDriver::ReadNativeScroll"),
    (Driver + "HandleMouse", "TeamModVersions.TeamVersionsDriver::ReadPhysicalWheel"),
    (Driver + "HandleMouse", "TeamModVersions.WheelRouter::Sample"),
    (Driver + "HandleMouse", "TeamModVersions.TeamVersionsDriver::LogInput"),
    (Driver + "HandleMouse", "TeamModVersions.TeamVersionsDriver::ResetInput"),
    (Driver + "SampleCursor", "CellMenu.CM_PageBase::get_CursorWorldPosition"),
    (Driver + "SampleCursor", "UnityEngine.Camera::WorldToScreenPoint"),
    (Driver + "SampleCursor", "TeamModVersions.PointerMath::TryGui"),
    (Driver + "SampleCursor", "TeamModVersions.TeamVersionsDriver::PointerCamera"),
    // 相机：GuiLayer 的菜单相机优先，CM_PageBase.Camera 只作后备（参考案实测为空）
    (Driver + "PointerCamera", "MainMenuGuiLayer::get_m_menuCamera"),
    (Driver + "PointerCamera", "CameraController::get_m_camera"),
    (Driver + "PointerCamera", "CellMenu.CM_PageBase::get_Camera"),
    ("TeamModVersions.LobbyButtons.Reconcile", "TeamModVersions.ButtonLayout::Place"),
    (Driver + "Usable", "UnityEngine.Behaviour::get_isActiveAndEnabled"),
    (Driver + "TryButton", "CellMenu.CM_PageBase::get_MainButtonStatus"),
    (Driver + "ReadNativeScroll", "CellMenu.CM_PageBase::get_ScrollInput"),
    // 物理滚轮：全插件唯一的读取点（只读、不捕获、不抢光标）
    (Driver + "ReadPhysicalWheel", "UnityEngine.Input::get_mouseScrollDelta"),
    // 开表 / 关闭 / 换页 / 卸载都必须复位按压与滚轮状态
    (Driver + "Toggle", "TeamModVersions.TeamVersionsDriver::ResetInput"),
    (Driver + "Close", "TeamModVersions.TeamVersionsDriver::ResetInput"),
    (Driver + "SetPage", "TeamModVersions.TeamVersionsDriver::ResetInput"),
};
var requiredMethods = new[]
{
    Driver + "Close", Driver + "RemoveButton", Driver + "SetPage", Driver + "Shutdown",
    Driver + "ReadNativeName", Driver + "ResolveNames", Driver + "DrawRuns",
    Driver + "PanelVisible", Driver + "PlaceButton",
    Driver + "HandleMouse", Driver + "SampleCursor", Driver + "PointerCamera", Driver + "TryButton",
    Driver + "ReadPhysicalButton", Driver + "SuppressLobbyPress", Driver + "SamePage",
    Driver + "ReadNativeScroll", Driver + "ReadPhysicalWheel", Driver + "ResetInput", Driver + "LogInput",
    Driver + "Table", Driver + "CloseLocalRect", Driver + "ListLocalRect", Driver + "CloseButtonRect",
};
var forbidden = new[]
{
    "CellMenu.CM_PlayerLobbyBar::get_m_inventorySlotItems", // live 1.1.5: broken generic TryGetValue stub
    "UnityEngine.Input::GetMouseButtonDown", "UnityEngine.Input::GetMouseButtonUp",
    "UnityEngine.Input::get_mousePosition", "UnityEngine.Input::GetKeyDown", "UnityEngine.Input::GetKeyUp", "UnityEngine.Input::GetKey",
    "UnityEngine.Input::GetAxis", "UnityEngine.Cursor::set_visible", "UnityEngine.Cursor::set_lockState",
    "UnityEngine.Screen::SetResolution",
    // 1.1.3: fatal native access violation on lobby join; never execute the old path, even in a test.
    "TeamModVersions.ExistingSteamSession::Ping", "Steamworks.SteamNetworkingIdentity::SetSteamID64",
    "Steamworks.SteamNetworkingMessages::GetSessionConnectionInfo",
    "Steamworks.SteamNetworkingMessages::SendMessageToUser", "Steamworks.SteamNetworkingSockets::ConnectP2P",
    "Steamworks.SteamMatchmaking::SendLobbyChatMsg",
    "Steamworks.SteamMatchmaking::SetLobbyData", "Steamworks.SteamNetworkingUtils::GetLocalPingLocation",
    "Steamworks.SteamNetConnectionInfo_t::get_m_addrRemote", "UnityEngine.Object::FindObjectsOfType",
    // 注：表头名字不开 richText —— 校验方式见 offline tests（只产出“文字+颜色”分段）与
    // DrawRuns 的 GUIStyle::CalcSize/GUI::Label 逐段绘制接线；这里不按 IL 常量判 richText 取值。
};

// 新契约：不是“禁止一切输入”，而是“输入只允许在一条清晰限定的路径上读取”。
//   * 物理滚轮 / 物理主键各只允许出现在一个方法里（只读、不捕获、不抢系统光标）；
//   * IMGUI 的自带滚轮滚动与 IMGUI 按压按钮不允许回到表里（它们会重复消费 / 在 GTFO 大厅里点不到）；
//   * 重建开表入口不得顺手关掉面板（面板可能正被读）。
var forbiddenPairs = new (string Caller, string Callee)[]
{
    (Driver + "Table", "UnityEngine.GUI::BeginScrollView"),
    (Driver + "Table", "UnityEngine.GUI::Button"),
    (Driver + "BuildStyles", "UnityEngine.GUISkin::get_button"),
    (Driver + "RemoveButton", Driver.TrimEnd('.') + "::Close"),
};
// 全插件唯一的读取点：Callee 只允许出现在 Only 这一个方法里
var singleSite = new (string Callee, string Only)[]
{
    ("UnityEngine.Input::get_mouseScrollDelta", Driver + "ReadPhysicalWheel"),
    ("UnityEngine.Input::GetMouseButton", Driver + "ReadPhysicalButton"),
    ("Steamworks.SteamNetworking::SendP2PPacket", "SteamIO.Send"),
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
Console.WriteLine("== 输入只允许在限定路径读取（不做全局捕获） ==");
foreach (var (callee, only) in singleSite)
{
    var sites = calls.Where(c => c.Callee == callee).Select(c => c.Caller).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
    bool ok = sites.Count == 1 && sites[0] == only;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {callee} 只出现在 {only}：{string.Join(", ", sites)}");
    if (!ok) failures++;
}

Console.WriteLine();
Console.WriteLine("== 必须不存在的 IMGUI 滚轮 / IMGUI 按压按钮 ==");
foreach (var (caller, callee) in forbiddenPairs)
{
    int hits = calls.Count(c => c.Caller == caller && c.Callee == callee);
    Console.WriteLine($"{(hits == 0 ? "PASS" : "FAIL")} {caller} -> {callee}：{hits} 处");
    if (hits != 0) failures++;
}

Console.WriteLine();
Console.WriteLine("== 鼠标每帧采样：必须在 1 Hz 刷新节流之前 ==");
// 按压边沿与物理滚轮都是“这一帧”的读数：HandleMouse 一旦落进 Update 里的 1 Hz 节流后面，
// 点击与滚轮就会以 1 次/秒采样（这就是本次热修修的缺陷），因此用 IL 偏移把顺序也钉住。
var mouseCallSites = calls.Where(c => c.Caller == Driver + "Update" && c.Callee == Driver.TrimEnd('.') + "::HandleMouse").ToList();
var throttleCallSites = calls.Where(c => c.Caller == Driver + "Update" && c.Callee == "UnityEngine.Time::get_realtimeSinceStartup").ToList();
string mouseAt = mouseCallSites.Count == 1 ? $"偏移 {mouseCallSites[0].Offset}" : $"缺失或重复（{mouseCallSites.Count} 处）";
string throttleAt = throttleCallSites.Count == 1 ? $"偏移 {throttleCallSites[0].Offset}" : $"缺失或重复（{throttleCallSites.Count} 处）";
bool mousePerFrame = mouseCallSites.Count == 1 && throttleCallSites.Count == 1 &&
    mouseCallSites[0].Offset < throttleCallSites[0].Offset;
Console.WriteLine($"{(mousePerFrame ? "PASS" : "FAIL")} Update → HandleMouse：{mouseAt}；1 Hz 节流读时间戳：{throttleAt}（前者必须更早）");
if (!mousePerFrame) failures++;
Console.WriteLine();
Console.WriteLine($"summary: calls={calls.Count} required={required.Length + requiredMethods.Length + forbiddenPairs.Length + singleSite.Length + 1} forbidden={forbidden.Length} failures={failures} -> {(failures == 0 ? "GREEN" : "RED")}");
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
