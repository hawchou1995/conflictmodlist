# 更新日志

## 1.0.2

- 修复：打开对照表时 `Font.CreateDynamicFontFromOSFont` 的 il2cpp interop 桩在 JIT 阶段抛 `TypeLoadException`（`Il2CppReferenceArray<System.String>` 泛型约束无效），该帧 `OnGUI` 完全无法执行、日志逐帧刷错。现改用 `new Font()` + `Font.Internal_CreateDynamicFont`，并按可读性优先挑选系统中文字体；字体创建失败（含类型加载/JIT 失败）只记录一次，退回界面默认字体继续画表。字体创建成功不代表该字体一定含简体中文字形；缺字形时本版不会自动换字体。
- 修复：1.0.1 在加载期崩溃、整个插件不加载。原因是 `Il2CppInterop` 的 `ClassInjector` 会把驱动类每个「声明的实例方法」的参数/返回类型丢给 `Il2CppClassPointerStore` 取原生 class 指针，而插件自有的 `ScreenPoint` 不是 il2cpp 类型，只能取回 `IntPtr.Zero`，`RegisterTypeInIl2Cpp` 抛 `NullReferenceException`（早于 `Harmony.PatchAll`）。现只让 `Update` / `OnGUI` 两个 Unity 消息回调进入 il2cpp 元数据，其余 21 个纯托管 helper 加 `[HideFromIl2Cpp]`；托管调用点与委托目标不变。
- 单人清单：只有一个真人（单机，或 1 真人 + BOT）时不再套用「隐藏版本一致项」，直接列出本机已加载的 BepInEx 插件与版本，标题为「本机模组清单」；多人时保留差异视角。
- 空状态分开：采集为空（一个插件都没读到）与筛选为空（有一致项但当前被隐藏）给不同提示；筛选为空时提示按 `F` 查看全部。
- 按键：`Esc` 关闭；`F` 在本次会话内切换「差异 / 全部」（不写配置文件）；`↑`/`↓`、`PgUp`/`PgDn`、`Home`/`End` 滚动与翻页，长清单能翻到底，不再依赖 IMGUI 鼠标滚轮。
- 原生关闭：在面板「关闭」按钮的位置放一个大厅原生 `CM_Item` 作为命中区，走大厅自己的虚拟光标、射线与 `OnBtnPress` 分发，不再依赖 IMGUI 鼠标。落点由大厅菜单相机把屏幕点投到父平面；屏幕空间画布用 `Canvas.scaleFactor` 等价换算，投影失败再退回画布中心换算。命中区的渲染组件全部关掉，视觉仍由 IMGUI 按钮提供；面板不可见（未打开 / 页面不活跃 / 屏幕过小）时命中区同步隐藏，不留透明拦截区。任一环节失败都退回 IMGUI 按钮与 `Esc`。
- 表头昵称颜色：优先读大厅原生 TMP 的逐字符结果（保留大厅改写后的大小写/缩写和逐字颜色），取不到时解析 TMP 原文里的颜色标签，最后才用原始昵称。IMGUI 侧关闭 richText，按「文字 + 颜色」分段绘制，远端昵称不进入富文本通道；本机列的「（你）」作为独立不染色段，不再被名字末尾的颜色标签染红。
- 测试源码：新增 `tests/EntryRegistrationGate`、`tests/LobbyWiringGate`，并扩充 `OfflineTests` 与 `tests/FontInteropGate`；全部离线运行，不启动游戏、不写 profile。
- 本版未做游戏内 GUI 与真实 Steam 多人的实测。

## 1.0.1

- 将模组名更正为 `conflictmodlist`。旧拼写版不再更新；安装新版前请卸载旧包或移走旧 DLL。
- 配置文件更名为 `BepInEx/config/hawchow.conflictmodlist.cfg`，配置键和大厅同步格式不变。
- 详情页新增「反馈与开黑」：青橘社区和 GTFO 小黑盒语音频道。
- 安装依赖更新为 BepInExPack_GTFO 3.2.4。

## 1.0.0

- 首次发布：装备大厅新增「模组版本对照」按钮，按玩家列出已加载的 BepInEx 模组及版本。
- 当前队伍中报告的最高版本标绿，较低版本标红；未同步、未报告和版本未知分别显示。
- 默认隐藏全队版本一致的模组。开启 `ShowMatchingMods` 后，在表尾单独显示并排序。
- 通过 Steam 大厅成员数据交换版本，已安装的玩家可直接互相对照。
