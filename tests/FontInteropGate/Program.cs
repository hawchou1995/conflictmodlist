using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using FontInteropGate;

// 生产路径回归闸门：检查"生产 conflictmodlist.dll"编译产物 + 真实 GTFO interop，不测算法、不做字符串 grep。
//   关卡 1（IL 精确调用点）：不得出现 UnityEngine.Font::CreateDynamicFontFromOSFont 调用点，
//        且必须出现 Font::Internal_CreateDynamicFont 调用点。
//   关卡 2（JIT/类型加载）：加载被测 DLL + 真实 interop，对 TeamVersionsDriver 每个方法以及它
//        IL 里 call/callvirt/newobj 到的每个方法执行 RuntimeHelpers.PrepareMethod（只 JIT 不执行）。
//        现场崩溃正是这一步：JIT 那个 interop 桩时抛
//        TypeLoadException: GenericArguments[0], 'System.String', on 'Il2CppReferenceArray`1[T]'
//        violates the constraint of type parameter 'T'（现场日志 1870 次，栈顶 Font.CreateDynamicFontFromOSFont）。
// 能力边界（不要夸大）：
//   * 只做类型加载/JIT，不执行原生 il2cpp。JIT 通过不等于运行时可调用：native 未实现、桩内部
//     运行时抛异常、参数非法等情况本闸门都排除不了。
//   * 不证明 GUI 真的画出来了，也不证明所选字体真有简体中文字形。
//   * IL 解码失败一律判红（显式失败），不会静默少扫调用点。
// 用法:
//   FontInteropGate <plugin.dll> <interopDir> <coreDir>
//   FontInteropGate --selftest <interopDir> <coreDir>      自检：IL 解码回归 + 真实 interop 负例

if (args.Length >= 1 && args[0] == "--selftest" && args.Length >= 3) return SelfTest(args);
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: FontInteropGate <plugin.dll> <interopDir> <coreDir> | --selftest <interopDir> <coreDir>");
    return 2;
}

const string ForbiddenMember = "CreateDynamicFontFromOSFont";
const string RequiredMember = "Internal_CreateDynamicFont";
const string FontType = "UnityEngine.Font";

string pluginPath = Path.GetFullPath(args[0]);
string interopDir = Path.GetFullPath(args[1]);
string coreDir = Path.GetFullPath(args[2]);
if (!File.Exists(pluginPath)) { Console.Error.WriteLine($"missing {pluginPath}"); return 2; }

Console.WriteLine($"plugin   : {pluginPath}");
Console.WriteLine($"sha256   : {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pluginPath)))}");
Console.WriteLine($"size     : {new FileInfo(pluginPath).Length} bytes");

var failures = new List<(string Target, Exception Error)>();

// ---------- 关卡 1: 编译产物 IL 精确调用点 ----------
var fontCallSites = new List<(string Caller, string Callee)>();
using (var stream = File.OpenRead(pluginPath))
using (var peReader = new PEReader(stream))
{
    var md = peReader.GetMetadataReader();
    foreach (var typeHandle in md.TypeDefinitions)
    {
        var typeDef = md.GetTypeDefinition(typeHandle);
        string typeName = FullName(md, typeDef);
        foreach (var methodHandle in typeDef.GetMethods())
        {
            var method = md.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0) continue;
            string label = $"{typeName}.{md.GetString(method.Name)}";
            IReadOnlyList<int> tokens;
            try { tokens = IlCalls.CallTokens(peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? Array.Empty<byte>()); }
            catch (Exception e) { failures.Add(($"IL {label}", e)); continue; }
            foreach (var token in tokens)
            {
                string callee = Describe(md, token);
                if (callee.StartsWith(FontType + "::")) fontCallSites.Add((label, callee));
            }
        }
    }
}
int forbidden = fontCallSites.Count(c => c.Callee == $"{FontType}::{ForbiddenMember}");
bool required = fontCallSites.Any(c => c.Callee == $"{FontType}::{RequiredMember}");

Console.WriteLine();
Console.WriteLine("== 关卡 1: 编译产物里 UnityEngine.Font 的调用点 ==");
foreach (var site in fontCallSites.OrderBy(c => c.Caller, StringComparer.Ordinal))
    Console.WriteLine($"   {site.Caller} -> {site.Callee}");
foreach (var failure in failures) Console.WriteLine($"   FAIL {failure.Target} :: {failure.Error.GetType().Name}: {failure.Error.Message}");
Console.WriteLine($"   forbidden {FontType}::{ForbiddenMember}: {forbidden} 处");
Console.WriteLine($"   required  {FontType}::{RequiredMember}: {(required ? "存在" : "缺失")}");

// ---------- 关卡 2: 真实 interop 下的 JIT / 类型加载 ----------
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    foreach (var dir in new[] { coreDir, interopDir })
    {
        string candidate = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(candidate)) return ctx.LoadFromAssemblyPath(candidate);
    }
    return null;
};

Console.WriteLine();
Console.WriteLine("== 关卡 2: JIT 驱动自身方法 + 它调用到的每个方法（真实 interop，不执行） ==");
int jitted = 0;
int unresolved = 0;
var reached = new Dictionary<MethodBase, string>();
try
{
    var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
    var driver = plugin.GetType("TeamModVersions.TeamVersionsDriver", throwOnError: true)!;
    var callers = driver.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                        .Cast<MethodBase>()
                        .Concat(driver.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                        .OrderBy(m => m.Name, StringComparer.Ordinal)
                        .ToList();
    foreach (var caller in callers)
    {
        Probe($"driver {caller.Name}", caller);
        byte[]? il = caller.GetMethodBody()?.GetILAsByteArray();
        if (il == null) continue;
        IReadOnlyList<int> tokens;
        try { tokens = IlCalls.CallTokens(il); }
        catch (Exception e) { failures.Add(($"IL driver {caller.Name}", e)); Console.WriteLine($"   FAIL IL driver {caller.Name} :: {e.GetType().Name}: {e.Message}"); continue; }
        foreach (var token in tokens)
        {
            MethodBase? callee;
            try { callee = caller.Module.ResolveMember(token) as MethodBase; }
            catch (Exception) { unresolved++; continue; }
            if (callee == null || callee.ContainsGenericParameters) { unresolved++; continue; }
            reached.TryAdd(callee, $"{callee.DeclaringType?.FullName}::{callee.Name}");
        }
    }
    foreach (var pair in reached.OrderBy(p => p.Value, StringComparer.Ordinal))
        Probe($"called {pair.Value}", pair.Key);
}
catch (Exception e)
{
    failures.Add(("<load>", e));
    Console.WriteLine($"   FAIL load :: {e.GetType().Name}: {e.Message}");
}

Console.WriteLine();
int red = forbidden + (required ? 0 : 1) + failures.Count;
Console.WriteLine($"summary: jitted={jitted} reached={reached.Count} unresolved={unresolved} failures={failures.Count} forbidden={forbidden} required={(required ? 1 : 0)} -> {(red == 0 ? "GREEN" : "RED")}");
foreach (var failure in failures.Take(5)) Console.WriteLine($"        failure: {failure.Target} :: {failure.Error.GetType().Name}: {failure.Error.Message.Split('\n')[0]}");
return red == 0 ? 0 : 1;

void Probe(string label, MethodBase method)
{
    if (method.IsAbstract || method.ContainsGenericParameters) return;
    try { RuntimeHelpers.PrepareMethod(method.MethodHandle); jitted++; Console.WriteLine($"   OK   {label}"); }
    catch (Exception e) { failures.Add((label, e)); Console.WriteLine($"   FAIL {label} :: {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
}

// ---------- 自检 ----------
int SelfTest(string[] argv)
{
    // 回归目标：
    //  1) OpCode.Value 的 short/ushort 归一化：双字节（0xFE 前缀）指令之后的 call/callvirt 必须仍被发现；
    //  2) 任何解码异常（未识别 opcode、截断、越界）必须显式失败，而不是 yield break 静默截断；
    //  3) 负例真命中现场异常：真实 interop 的 Font.CreateDynamicFontFromOSFont 必须抛 TypeLoadException，
    //     而修复所用的 Font..ctor / Font.Internal_CreateDynamicFont 必须能通过 JIT。
    int checks = 0, failed = 0;
    void Check(bool ok, string message)
    {
        checks++;
        if (!ok) failed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {checks}: {message}");
    }
    void Decodes(byte[] il, int[] expected, string message)
    {
        try
        {
            var got = IlCalls.CallTokens(il).ToArray();
            Check(got.SequenceEqual(expected), $"{message} (tokens=[{string.Join(",", got.Select(t => "0x" + t.ToString("X8")))}])");
        }
        catch (Exception e) { Check(false, $"{message} :: 意外抛错 {e.GetType().Name}: {e.Message}"); }
    }
    void Rejects(byte[] il, string message)
    {
        try { IlCalls.CallTokens(il); Check(false, $"{message} :: 未抛错（等于静默截断）"); }
        catch (IlCalls.DecodeException e) { Check(true, $"{message} :: {e.Message}"); }
        catch (Exception e) { Check(false, $"{message} :: 抛了非 DecodeException：{e.GetType().Name}"); }
    }

    Decodes(new byte[] { 0xFE, 0x01, 0x28, 0x01, 0x00, 0x00, 0x0A }, new[] { 0x0A000001 },
        "双字节 ceq(0xFE01) 之后的 call 必须被发现");
    Decodes(new byte[] { 0xFE, 0x06, 0x01, 0x00, 0x00, 0x0A, 0x6F, 0x02, 0x00, 0x00, 0x0A }, new[] { 0x0A000002 },
        "双字节 ldftn(0xFE06，4 字节操作数) 之后的 callvirt 必须被发现（ldftn 本身不是调用点）");
    Decodes(new byte[] { 0x73, 0x03, 0x00, 0x00, 0x06 }, new[] { 0x06000003 },
        "单字节 newobj 的 token 必须被发现");
    Rejects(new byte[] { 0x28, 0x01, 0x00 }, "call 的方法 token 不足 4 字节");
    Rejects(new byte[] { 0xFE }, "孤立的 0xFE 前缀");
    Rejects(new byte[] { 0x45, 0x02, 0x00, 0x00, 0x00 }, "switch 目标表越界");
    byte unknown = 0xFF;
    for (int v = 0; v < 0xFE && unknown == 0xFF; v++)
        if (!IlCalls.KnownOpcodes.Contains((ushort)v)) unknown = (byte)v;
    Check(unknown != 0xFF, "能找到未分配的 opcode 字节用于负例");
    if (unknown != 0xFF) Rejects(new byte[] { unknown }, "未识别的 opcode");

    string interop = Path.GetFullPath(argv[1]), core = Path.GetFullPath(argv[2]);
    AssemblyLoadContext.Default.Resolving += (ctx, name) =>
    {
        foreach (var dir in new[] { core, interop })
        {
            string candidate = Path.Combine(dir, name.Name + ".dll");
            if (File.Exists(candidate)) return ctx.LoadFromAssemblyPath(candidate);
        }
        return null;
    };
    var font = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(interop, "UnityEngine.TextRenderingModule.dll"))
                                            .GetType("UnityEngine.Font", throwOnError: true)!;
    var broken = font.GetMethod("CreateDynamicFontFromOSFont", new[] { typeof(string), typeof(int) })!;
    try
    {
        RuntimeHelpers.PrepareMethod(broken.MethodHandle);
        Check(false, "负例：Font.CreateDynamicFontFromOSFont(string,int) 竟能 JIT（现场本应 TypeLoadException）");
    }
    catch (TypeLoadException e) { Check(e.Message.Contains("constraint"), $"负例真命中现场异常 TypeLoadException：{e.Message.Split('\n')[0]}"); }
    catch (Exception e) { Check(false, $"负例：抛了非 TypeLoadException：{e.GetType().Name}: {e.Message.Split('\n')[0]}"); }

    foreach (var replacement in new MethodBase[] { font.GetConstructor(Type.EmptyTypes)!, font.GetMethod("Internal_CreateDynamicFont")! })
    {
        try { RuntimeHelpers.PrepareMethod(replacement.MethodHandle); Check(true, $"修复路径 {replacement.Name} 可 JIT"); }
        catch (Exception e) { Check(false, $"修复路径 {replacement.Name} 不能 JIT：{e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
    }

    Console.WriteLine();
    Console.WriteLine($"selftest: checks={checks} failed={failed} -> {(failed == 0 ? "GREEN" : "RED")}");
    return failed == 0 ? 0 : 1;
}

// ---------- helpers ----------
static string FullName(MetadataReader md, TypeDefinition type)
{
    string ns = md.GetString(type.Namespace), name = md.GetString(type.Name);
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

static string TypeRefName(MetadataReader md, TypeReferenceHandle handle)
{
    var tr = md.GetTypeReference(handle);
    string ns = md.GetString(tr.Namespace), name = md.GetString(tr.Name);
    return ns.Length == 0 ? name : ns + "." + name;
}
