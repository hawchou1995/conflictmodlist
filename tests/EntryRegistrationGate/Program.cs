using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FontInteropGate;

// 入口注册闸门：检查生产 conflictmodlist.dll 的「注入类注册签名」，不启动游戏、不写 profile。
//
// 现场故障（日志 754-760）：BepInEx 加载 conflictmodlist 1.0.1 时，
//   Plugin.Load → IL2CPPChainloader.AddUnityComponent<TeamVersionsDriver> → Il2CppUtils.AddComponent
//   → ClassInjector.RegisterTypeInIl2Cpp → ClassInjector.ConvertMethodInfo 抛 NullReferenceException，
// 崩在 PatchAll 之前，插件整体不加载。
//
// 本闸门复现“注册签名”这一步，机制与本地真实 Il2CppInterop.Runtime.dll 一致：
//   1) 枚举：ClassInjector 对本类型用
//      GetMethods(BindingFlags.DeclaredOnly | Instance | Public | NonPublic)（本地反编译 ClassInjector.cs:249）。
//      类型枚举本身不吞异常：出现 ReflectionTypeLoadException 一律脱敏打印并判红，且必须能看到目标类型。
//   2) 过滤：IsMethodEligible（同文件 471-518）—— Finalize / static / 方法或属性或事件上的
//      [HideFromIl2Cpp] / IsTypeSupported(返回类型 + 每个参数)（444-455）。
//   3) 解析：ConvertMethodInfo（535-627）对每个进入元数据的方法，把「返回类型」与「每个参数（byref 取元素）」
//      交给 Il2CppClassPointerStore.GetNativeClassPointer 去取原生 Il2CppClass*。取回 IntPtr.Zero 表示该类型
//      在 il2cpp 里没有 class：ConvertMethodInfo 会把这个空指针交给 il2cpp_class_get_type()，
//      而 byref 分支（559-571）还会解引用它的结果（nativeStruct.Data/Attrs/Type/Pinned）。
//      取指针的结论分五类，只有两类算证据、一类算有据的离线限制，其余一律判红：
//        builtin         void / 基元 / string —— 由 Il2Cppmscorlib 提供，离线不必探测原生库，也不做任何判断
//        zero            已证 IntPtr.Zero —— 判红（空指针进 il2cpp_class_get_type）
//        nonzero         已证非零指针 —— 通过（仅当原生环境可跑时才会出现）
//        native-required 已知外部 interop 类型（程序集来自 interop/core 白名单）+ 内层异常明确是“缺离线原生/工具链依赖”
//                        （DllNotFound / EntryPointNotFound / BadImageFormat / GameAssembly、
//                         或 Microsoft.Extensions.Logging.Abstractions 这类 BepInEx 侧依赖缺失）—— 记为有据的离线限制
//        unproven        其它任何异常（含插件自有类型初始化异常、语义不明的 TypeInitializationException）—— 判红
//   4) 边界（按实际注入器，不扩大扫描）：字段只认 `Il2CppReferenceField<>` / `Il2CppValueField<>` / `Il2CppStringField`
//      三种包装（ClassInjector.cs:457-469 `IsFieldEligible`），普通 C# 字段一律被过滤；构造函数不在
//      `GetMethods` 里，注入器用 `CreateEmptyCtor` 现造 `.ctor(IntPtr)`（629-...）。因此本闸门不扫字段、不扫构造。
//   5) 合规：Unity 需要的消息回调（Update / OnGUI）必须仍然进元数据；managed 侧入口（本程序集里对驱动的
//      IL 调用点，加上 ConvertDelegate 的委托目标）必须仍然存在且可调用 —— [HideFromIl2Cpp] 只影响 il2cpp
//      元数据，不改变 C# 调用。
//
// 能力边界（不要夸大）：
//   * 只做静态注册签名验证：不加载 GameAssembly、不真实注册类，所以“原生启动后确实能加组件”仍需实机验证。
//   * 不用 JIT 通过冒充原生注册通过：这里判的是类型解析结果，不是 JIT。
//   * 不检查布局 / 可见性 / 清单 / 颜色 / 关闭逻辑（OfflineTests、LobbyWiringGate 各自负责）。
//
// 用法:
//   EntryRegistrationGate <plugin.dll> <interopDir> <coreDir>
//   EntryRegistrationGate --selftest <interopDir> <coreDir>

if (args.Length >= 1 && args[0] == "--selftest")
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: EntryRegistrationGate --selftest <interopDir> <coreDir>");
        return 2;
    }
    return SelfTest(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
}

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: EntryRegistrationGate <plugin.dll> <interopDir> <coreDir> | --selftest <interopDir> <coreDir>");
    return 2;
}

string pluginPath = Path.GetFullPath(args[0]);
string interopDir = Path.GetFullPath(args[1]);
string coreDir = Path.GetFullPath(args[2]);
if (!File.Exists(pluginPath)) { Console.Error.WriteLine($"missing {pluginPath}"); return 2; }

const string TargetType = "TeamModVersions.TeamVersionsDriver";

Console.WriteLine($"plugin   : {pluginPath}");
Console.WriteLine($"sha256   : {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pluginPath)))}");
Console.WriteLine($"interop  : {interopDir}");
Console.WriteLine($"core     : {coreDir}");

AddResolver(interopDir, coreDir);

int failures = 0;
int checks = 0;
void Check(bool ok, string message)
{
    checks++;
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {message}");
}

Assembly plugin;
try { plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath); }
catch (Exception e) { Console.Error.WriteLine($"FAIL cannot load plugin: {e.GetType().Name}: {Sanitize(e.Message)}"); return 1; }

var runtime = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(coreDir, "Il2CppInterop.Runtime.dll"));
var storeType = runtime.GetType("Il2CppInterop.Runtime.Il2CppClassPointerStore", throwOnError: true)!;
var getPointer = storeType.GetMethod("GetNativeClassPointer", BindingFlags.Public | BindingFlags.Static)!;
var injectorType = runtime.GetType("Il2CppInterop.Runtime.Injection.ClassInjector", throwOnError: true)!;
var realEligible = injectorType.GetMethod("IsMethodEligible", BindingFlags.NonPublic | BindingFlags.Static);
var objectBase = runtime.GetType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase", throwOnError: true)!;
var monoBehaviour = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(interopDir, "UnityEngine.CoreModule.dll"))
                                                .GetType("UnityEngine.MonoBehaviour", throwOnError: true)!;

// ---------- 注入类型（loader 异常不静默，目标类型硬检查） ----------
var types = new List<Type>();
string? typeLoadFailure = null;
try
{
    types.AddRange(plugin.GetTypes());
}
catch (ReflectionTypeLoadException e)
{
    types.AddRange(e.Types.Where(t => t != null)!);
    typeLoadFailure = DescribeTypeLoadFailure(e);
}
Check(typeLoadFailure == null, typeLoadFailure == null
    ? "程序集类型枚举完整（没有 ReflectionTypeLoadException）"
    : $"程序集类型枚举不完整，不能当作通过 :: {typeLoadFailure}");
Check(HasTargetType(types, TargetType), $"目标注入类型在被检集合里：{TargetType}");
if (!HasTargetType(types, TargetType)) return 1;

var injected = types.Where(t => monoBehaviour.IsAssignableFrom(t)).OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
Console.WriteLine();
Console.WriteLine("== 注入类型（Il2CppInterop 会为它们建 il2cpp 类，逐方法转换签名） ==");
foreach (var t in injected) Console.WriteLine($"   {t.FullName} : {t.BaseType?.FullName}");
Check(injected.Count > 0, $"注入类型集合非空（{injected.Count}）");
Check(injected.Any(t => t.FullName == TargetType), $"{TargetType} 是 MonoBehaviour 派生（会被 AddComponent 注入）");
if (injected.Count == 0) return 1;

// ---------- managed 侧调用点（IL 精确扫描，供“managed 回调仍可调用”判定） ----------
var externalCalls = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
try
{
    using var stream = File.OpenRead(pluginPath);
    using var pe = new PEReader(stream);
    var md = pe.GetMetadataReader();
    foreach (var typeHandle in md.TypeDefinitions)
    {
        var typeDef = md.GetTypeDefinition(typeHandle);
        string callerType = FullName(md, typeDef);
        foreach (var methodHandle in typeDef.GetMethods())
        {
            var method = md.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0) continue;
            string caller = $"{callerType}.{md.GetString(method.Name)}";
            IReadOnlyList<int> tokens;
            try { tokens = IlCalls.CallTokens(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? Array.Empty<byte>()); }
            catch (Exception e) { throw new InvalidOperationException($"IL 解码失败 {caller}: {e.Message}"); }
            foreach (var token in tokens)
            {
                var (owner, name) = Describe(md, token);
                if (owner == callerType) continue;   // 驱动自己内部调用不算“外部入口”
                if (!externalCalls.TryGetValue(owner, out var set))
                    externalCalls[owner] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(name);
            }
        }
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL IL 扫描失败（不能少扫 managed 调用点）：{e.GetType().Name}: {e.Message}");
    return 1;
}

// ---------- 与本地真实实现对齐的过滤 ----------
bool HideMarked(MemberInfo member) =>
    member.CustomAttributes.Any(a => a.AttributeType.FullName == "Il2CppInterop.Runtime.Attributes.HideFromIl2CppAttribute");

bool TypeSupported(Type t)
{
    if (t.IsValueType || t == typeof(string) || t.IsGenericParameter) return true;   // ClassInjector.cs:444-449
    if (t.IsByRef) return TypeSupported(t.GetElementType()!);                        // 450-453
    return objectBase.IsAssignableFrom(t);                                           // 454
}

bool ReplicaEligible(MethodInfo m)
{
    if (m.Name == "Finalize") return false;                                          // 473-476
    if (m.IsStatic) return false;                                                    // 477-480
    if (HideMarked(m)) return false;                                                 // 481-484
    var owner = m.DeclaringType;
    if (owner != null)
    {
        foreach (var p in owner.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (p.GetAccessors(nonPublic: true).Contains(m) && HideMarked(p)) return false;                     // 487-492
        foreach (var ev in owner.GetEvents(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if ((ev.GetAddMethod(true) == m || ev.GetRemoveMethod(true) == m) && HideMarked(ev)) return false;  // 493-500
    }
    if (!TypeSupported(m.ReturnType)) return false;                                  // 502-506
    foreach (var p in m.GetParameters()) if (!TypeSupported(p.ParameterType)) return false;   // 507-516
    return true;
}

// 真实 ClassInjector.IsMethodEligible 打日志时会碰 Microsoft.Extensions.Logging.Abstractions，离线环境通常取不到；
// 取不到就用上面的同规则复刻，并在输出里明说（绝不因此把“有风险”判成“通过”）。
string realFilterState = realEligible == null ? "missing" : "ready";

// ---------- 类型解析：真实 Il2CppClassPointerStore + 元数据白名单 ----------
bool FromIl2CppAssembly(Type t)
{
    if (PrimitiveLike(t)) return true;
    string module = t.Module.Name;
    return File.Exists(Path.Combine(interopDir, module)) || File.Exists(Path.Combine(coreDir, module));
}

var pointerCache = new Dictionary<Type, (string State, string Note)>();
(string State, string Note) Probe(Type t)
{
    if (pointerCache.TryGetValue(t, out var cached)) return cached;
    var store = InvokeStore(getPointer, t);
    var result = ClassifyOutcome(t, FromIl2CppAssembly(t), store.Pointer, store.Error);
    pointerCache[t] = result;
    return result;
}

static string Name(Type t) => t.FullName ?? t.Name;

static string Signature(MethodInfo m) =>
    $"{Name(m.ReturnType)} {m.Name}(" +
    string.Join(", ", m.GetParameters().Select(p => (p.ParameterType.IsByRef ? "ref " : "") + Name(p.ParameterType))) + ")";

// ---------- 逐注入类型：注册签名检查 ----------
var stateCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
var nativeRequiredTypes = new SortedSet<string>(StringComparer.Ordinal);

foreach (var type in injected)
{
    Console.WriteLine();
    Console.WriteLine($"== {type.FullName}: ClassInjector 会转换的签名 ==");

    var declared = type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                       .OrderBy(m => m.MetadataToken)
                       .ToList();
    var eligible = new List<MethodInfo>();
    int skipped = 0;
    int comparisons = 0;
    int disagreements = 0;

    foreach (var m in declared)
    {
        bool replica = ReplicaEligible(m);
        bool? real = null;
        if (realEligible != null && realFilterState == "ready")
        {
            try { real = (bool)realEligible.Invoke(null, new object[] { m })!; }
            catch (TargetInvocationException e)
            {
                string reason = e.InnerException?.GetType().Name ?? "unknown";
                string text = Sanitize(e.InnerException?.Message);
                realFilterState = $"unavailable offline: {reason}: {text}";
            }
            catch (Exception e) { realFilterState = $"unavailable offline: {e.GetType().Name}"; }
        }
        if (real.HasValue) { comparisons++; if (real.Value != replica) disagreements++; }
        if (replica) eligible.Add(m); else skipped++;
        Console.WriteLine($"   {(replica ? "META" : "skip")} {Signature(m)}" +
                          (HideMarked(m) ? "  [HideFromIl2Cpp]" : "") +
                          (real.HasValue ? $"  (real={real.Value})" : ""));
    }

    if (comparisons > 0)
        Check(disagreements == 0, $"{type.Name}: 过滤规则与本地真实 IsMethodEligible 一致（对照 {comparisons} 个方法，分歧 {disagreements}）");
    else
        Console.WriteLine("   NOTE 真实 IsMethodEligible 在本机不可用（缺 Microsoft.Extensions.Logging.Abstractions），过滤按反编译同规则复刻；方法集合与风险检查不受影响");
    Check(eligible.Count > 0, $"{type.Name}: 进入 il2cpp 元数据的方法集合非空（{eligible.Count} 个，跳过 {skipped} 个）");

    // 逐签名检查：返回类型 + 每个参数（byref 取元素）
    var hazards = new List<string>();
    foreach (var m in eligible)
    {
        var elements = new List<(string Where, Type Type)> { ("返回类型", m.ReturnType) };
        foreach (var p in m.GetParameters())
            elements.Add(($"参数 {p.Name}", p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType));

        foreach (var (where, t) in elements)
        {
            if (!TypeSupported(t) && !t.IsByRef) continue;   // 已被 IsMethodEligible 挡掉，不会进 ConvertMethodInfo
            var (state, note) = Probe(t);
            stateCounts[state] = stateCounts.TryGetValue(state, out int n) ? n + 1 : 1;
            bool structural = FromIl2CppAssembly(t);
            if (state == "native-required") nativeRequiredTypes.Add(Name(t));
            if (state is "zero" or "unproven")
                hazards.Add($"{m.Name} :: {Signature(m)} :: {where} = {Name(t)} :: [{state}] {note}");
            else if (!structural)
                hazards.Add($"{m.Name} :: {Signature(m)} :: {where} = {Name(t)} :: [foreign-assembly] 类型来自非 il2cpp 程序集（模块 {t.Module.Name}），取不到原生 Il2CppClass*");
        }
    }

    if (hazards.Count == 0)
        Check(true, $"{type.Name}: 所有进入元数据的签名都能解析到原生 class（无空指针 / 无未证实异常）");
    else
        foreach (var h in hazards)
            Check(false, $"{type.Name}: 注册签名有风险 —— {h}");

    // Unity 需要的消息回调必须仍然注册（ClassInjector 把它们放进 il2cpp 方法表，Unity 按名字找）。
    foreach (var callback in new[] { "Update", "LateUpdate", "OnGUI" })
    {
        bool registered = eligible.Any(m => m.Name == callback && m.ReturnType == typeof(void) && m.GetParameters().Length == 0);
        Check(registered, $"{type.Name}: Unity 消息回调仍进元数据 Void {callback}()");
    }

    // managed 侧入口：本程序集里对该驱动的 IL 调用点必须仍然存在且可调用（[HideFromIl2Cpp] 不影响 C# 调用）。
    var called = externalCalls.TryGetValue(type.FullName!, out var set) ? set : new SortedSet<string>(StringComparer.Ordinal);
    Check(called.Count > 0, $"{type.Name}: 程序集内对驱动的 managed 调用点集合非空（{called.Count}）");
    foreach (var name in called.Where(n => !n.StartsWith('.') && n != "Finalize"))
    {
        var hit = declared.FirstOrDefault(m => m.Name == name) ?? type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(m => m.Name == name);
        Check(hit != null && !hit.IsAbstract, $"{type.Name}: managed 入口仍可调用（程序集内有调用点）{name}");
    }

    // 入口接收实名单选择的玩家 ID；旧的 int 原生关闭代理已移除。
    foreach (var (name, paramType) in new[] { ("Toggle", "System.UInt64"), ("CloseFromPress", "System.Int32") })
    {
        var hit = declared.FirstOrDefault(m => m.Name == name);
        if (hit == null)
        {
            Console.WriteLine($"   NOTE 该构建没有 {name}（旧版没有对应的原生回调），不判红");
            continue;
        }
        bool ok = !hit.IsAbstract && hit.ReturnType == typeof(void)
                  && hit.GetParameters().Length == 1 && Name(hit.GetParameters()[0].ParameterType) == paramType;
        Check(ok, $"{type.Name}: 委托目标签名不变 Void {name}({paramType})");
    }
}

Console.WriteLine();
if (nativeRequiredTypes.Count > 0)
    Console.WriteLine($"native-required（有据的离线限制，非失败）：{string.Join(", ", nativeRequiredTypes)}");
Console.WriteLine($"signature states: {string.Join(" ", stateCounts.Select(kv => kv.Key + "=" + kv.Value))}");
Console.WriteLine($"filter: {realFilterState}");
Console.WriteLine($"summary: injectedTypes={injected.Count} checks={checks} failures={failures} -> {(failures == 0 ? "GREEN" : "RED")}");
Console.WriteLine("capability: 静态注册签名验证；不启动 il2cpp，不代替实机原生启动验证。");
return failures == 0 ? 0 : 1;

// ---------------------------------------------------------------- self test

static int SelfTest(string interopDir, string coreDir)
{
    AddResolver(interopDir, coreDir);

    int checks = 0, failed = 0;
    void Expect(bool ok, string message)
    {
        checks++;
        if (!ok) failed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {checks}: {message}");
    }

    var runtime = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(coreDir, "Il2CppInterop.Runtime.dll"));
    var getPointer = runtime.GetType("Il2CppInterop.Runtime.Il2CppClassPointerStore", throwOnError: true)!
                           .GetMethod("GetNativeClassPointer", BindingFlags.Public | BindingFlags.Static)!;
    bool Provided(Type t) { string m = t.Module.Name; return File.Exists(Path.Combine(interopDir, m)) || File.Exists(Path.Combine(coreDir, m)); }

    var vector3 = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(interopDir, "UnityEngine.CoreModule.dll"))
                                       .GetType("UnityEngine.Vector3", throwOnError: true)!;

    // 1) void / 基元：归为 builtin，不碰原生库，也不因此“判红”
    Expect(IsBuiltin(typeof(void)) && IsBuiltin(typeof(int)) && IsBuiltin(typeof(string)) && !IsBuiltin(typeof(GateOwnedStruct)),
        "IsBuiltin：void/int/string 是 builtin，插件自有 struct 不是");
    Expect(ClassifyOutcome(typeof(int), true, null, null).State == "builtin",
        "builtin 类型在没有原生环境、没有任何取指针结果时仍归为 builtin（不做判断，也不算失败）");

    // 2) 已证 zero：插件自有值类型（模块不在 interop/core）取回空指针 —— 必须判红
    Expect(!Provided(typeof(GateOwnedStruct)), "GateOwnedStruct 的模块不在 interop/core 白名单（模拟插件自有值类型）");
    Expect(ClassifyOutcome(typeof(GateOwnedStruct), false, IntPtr.Zero, null).State == "zero",
        "已证 IntPtr.Zero → zero（判红）");

    // 3) 负例：任意 TypeInitializationException 不得被当作“需要原生运行时”而通过
    var pluginInitFailure = new TypeInitializationException("GateOwnedStruct", new NullReferenceException("boom"));
    Expect(ClassifyOutcome(typeof(GateOwnedStruct), false, null, pluginInitFailure).State == "unproven",
        "插件自有类型初始化异常（TypeInitializationException ← NullReferenceException）→ unproven（判红），不是 native-required");

    // 4) 有据的离线限制：已知外部 interop 类型 + 明确缺离线依赖
    var offlineDependency = new TypeInitializationException("UnityEngine.Vector3",
        new FileNotFoundException("Could not load file or assembly 'Microsoft.Extensions.Logging.Abstractions'"));
    Expect(ClassifyOutcome(vector3, true, null, offlineDependency).State == "native-required",
        "interop 白名单类型 + 内层 FileNotFoundException(Microsoft.Extensions.Logging.Abstractions) → native-required（有据，非失败）");
    Expect(ClassifyOutcome(vector3, true, null,
            new TypeInitializationException("UnityEngine.Vector3", new DllNotFoundException("GameAssembly"))).State == "native-required",
        "interop 白名单类型 + 内层 DllNotFoundException(GameAssembly) → native-required");
    Expect(ClassifyOutcome(vector3, true, null,
            new TypeInitializationException("UnityEngine.Vector3", new NullReferenceException("boom"))).State == "unproven",
        "interop 类型 + 语义不明的初始化异常 → unproven（不把任意 TypeInitializationException 当作成功）");

    // 5) 真实取指针：插件自有值类型 = 干净 zero；interop 类型 = 缺离线依赖
    var owned = InvokeStore(getPointer, typeof(GateOwnedStruct));
    Expect(owned is { Pointer: IntPtr p } && p == IntPtr.Zero,
        $"真实 Il2CppClassPointerStore 对插件自有 struct 返回 IntPtr.Zero（state={ClassifyOutcome(typeof(GateOwnedStruct), Provided(typeof(GateOwnedStruct)), owned.Pointer, owned.Error).State}）");
    var interopProbe = InvokeStore(getPointer, vector3);
    Expect(interopProbe.Pointer == null && interopProbe.Error != null &&
           ClassifyOutcome(vector3, true, interopProbe.Pointer, interopProbe.Error).State == "native-required",
        $"真实 Il2CppClassPointerStore 对 UnityEngine.Vector3 抛缺离线依赖（state={ClassifyOutcome(vector3, true, interopProbe.Pointer, interopProbe.Error).State}）");

    // 6) 类型枚举不静默：loader 异常必须给出脱敏说明；目标类型缺失必须判红
    var loaderFailure = new ReflectionTypeLoadException(
        new Type?[] { typeof(int), null },
        new Exception[] { new TypeLoadException(@"Could not load type 'X' from C:\Users\someone\BepInEx\plugins\conflictmodlist.dll") });
    var described = DescribeTypeLoadFailure(loaderFailure);
    Expect(described.Length > 0 && described.Contains("1 个类型加载失败") && described.Contains("TypeLoadException"),
        $"ReflectionTypeLoadException → 非空脱敏说明（{described}）");
    Expect(!described.Contains("someone") && described.Contains("<path>"),
        "脱敏：绝对路径被替换为 <path>，不泄漏个人目录");
    Expect(!HasTargetType(new Type?[] { null, typeof(int) }, "TeamModVersions.TeamVersionsDriver"),
        "HasTargetType：集合里没有目标类型 → 判红（硬检查）");
    Expect(HasTargetType(new Type?[] { typeof(int), null }, "System.Int32"),
        "HasTargetType：能定位存在的类型（对照用）");

    Console.WriteLine();
    Console.WriteLine($"selftest: checks={checks} failed={failed} -> {(failed == 0 ? "GREEN" : "RED")}");
    return failed == 0 ? 0 : 1;
}

// ---------------------------------------------------------------- helpers

static void AddResolver(string interopDir, string coreDir) =>
    AssemblyLoadContext.Default.Resolving += (ctx, name) =>
    {
        foreach (var dir in new[] { coreDir, interopDir })
        {
            string candidate = Path.Combine(dir, name.Name + ".dll");
            if (File.Exists(candidate)) return ctx.LoadFromAssemblyPath(candidate);
        }
        return null;
    };

static bool PrimitiveLike(Type t) => t == typeof(void) || t == typeof(string) || t.IsPrimitive || t.IsGenericParameter;

// void / 基元 / string 由 Il2Cppmscorlib 提供（Il2CppClassPointerStore.GetNativeClassPointer 对它们有专门分支），
// 离线环境不需要也不能去探测原生库；因此单独归类，既不当成功也不当失败。
static bool IsBuiltin(Type t) => t == typeof(void) || t == typeof(string) || t.IsPrimitive;

static bool HasTargetType(IEnumerable<Type?> types, string fullName) =>
    types.Any(t => t != null && string.Equals(t.FullName, fullName, StringComparison.Ordinal));

// 只允许“明确缺原生库 / 明确缺 BepInEx 侧离线依赖”的内层异常算作有据的离线限制。
static bool ChainShowsOfflineNative(Exception? error)
{
    for (var e = error; e != null; e = e.InnerException)
    {
        if (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) return true;
        string message = e.Message ?? string.Empty;
        if (message.Contains("GameAssembly", StringComparison.OrdinalIgnoreCase)) return true;
        if (e is FileNotFoundException && message.Contains("Microsoft.Extensions.Logging", StringComparison.OrdinalIgnoreCase)) return true;
    }
    return false;
}

static string DescribeError(Exception? error)
{
    var parts = new List<string>();
    for (var e = error; e != null && parts.Count < 4; e = e.InnerException)
        parts.Add($"{e.GetType().Name}: {Sanitize(e.Message)}");
    return parts.Count == 0 ? "<无异常信息>" : string.Join(" ← ", parts);
}

// 取指针结论只有三类是证据/有据限制，其余（尤其语义不明的初始化异常）一律 unproven → 判红。
static (string State, string Note) ClassifyOutcome(Type type, bool il2cppProvided, IntPtr? pointer, Exception? error)
{
    if (IsBuiltin(type)) return ("builtin", "void/基元/string 由 Il2Cppmscorlib 提供，离线无需原生探测");
    if (pointer is IntPtr p)
        return p == IntPtr.Zero
            ? ("zero", "Il2CppClassPointerStore.GetNativeClassPointer 返回 IntPtr.Zero（该类型在 il2cpp 里没有 class）")
            : ("nonzero", "已证原生 class 指针非零");
    if (error == null) return ("unproven", "既没有返回值也没有异常，无法证明可解析");
    if (!il2cppProvided)
        return ("unproven", $"类型来自非 il2cpp 程序集（模块 {type.Module.Name}），离线取不到原生 class 指针 :: {DescribeError(error)}");
    if (ChainShowsOfflineNative(error))
        return ("native-required", $"已知外部 interop 类型（模块 {type.Module.Name}），离线无法初始化原生/工具链依赖 :: {DescribeError(error)}");
    return ("unproven", $"未证实的取指针异常（不当作可解析） :: {DescribeError(error)}");
}

static (IntPtr? Pointer, Exception? Error) InvokeStore(MethodInfo getPointer, Type type)
{
    try
    {
        object? value = getPointer.Invoke(null, new object[] { type });
        return (value is IntPtr p ? p : (IntPtr?)null, null);
    }
    catch (TargetInvocationException e)
    {
        return (null, e.InnerException ?? e);
    }
    catch (Exception e)
    {
        return (null, e);
    }
}

static string Sanitize(string? message)
{
    string line = (message ?? string.Empty).Split('\n')[0].Trim();
    line = Regex.Replace(line, @"[A-Za-z]:\\[^\s'""]*", "<path>");
    line = Regex.Replace(line, @"(^|\s)/(?:[^\s/]+/)+[^\s'""]*", " <path>");
    return line.Length > 160 ? line[..160] + "…" : line;
}

static string DescribeTypeLoadFailure(ReflectionTypeLoadException e)
{
    int nulls = e.Types.Count(t => t == null);
    var loaders = e.LoaderExceptions.Where(x => x != null)
                                    .Select(x => $"{x!.GetType().Name}: {Sanitize(x.Message)}")
                                    .Distinct()
                                    .Take(5);
    string detail = string.Join(" | ", loaders);
    return $"{nulls} 个类型加载失败" + (detail.Length == 0 ? "（没有 loader 异常明细）" : $"；loader: {detail}");
}

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

static (string Owner, string Method) Describe(MetadataReader md, int token)
{
    var handle = MetadataTokens.EntityHandle(token);
    switch (handle.Kind)
    {
        case HandleKind.MemberReference:
        {
            var mr = md.GetMemberReference((MemberReferenceHandle)handle);
            string owner = mr.Parent.Kind switch
            {
                HandleKind.TypeReference => TypeRefName(md, (TypeReferenceHandle)mr.Parent),
                HandleKind.TypeDefinition => FullName(md, md.GetTypeDefinition((TypeDefinitionHandle)mr.Parent)),
                _ => "<typespec>",
            };
            return (owner, md.GetString(mr.Name));
        }
        case HandleKind.MethodDefinition:
        {
            var m = md.GetMethodDefinition((MethodDefinitionHandle)handle);
            return (FullName(md, md.GetTypeDefinition(m.GetDeclaringType())), md.GetString(m.Name));
        }
        case HandleKind.MethodSpecification:
        {
            var spec = md.GetMethodSpecification((MethodSpecificationHandle)handle);
            return Describe(md, MetadataTokens.GetToken(spec.Method));
        }
        default:
            return ("<" + handle.Kind + ">", "<unknown>");
    }
}

// 自检用的“插件自有值类型”替身：模块是闸门自身（不在 interop/core 白名单），用来验证 zero / unproven 两个负例。
struct GateOwnedStruct
{
}
