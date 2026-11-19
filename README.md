# conflictmodlist

![conflictmodlist 图标](https://raw.githubusercontent.com/hawchou1995/conflictmodlist/main/packaging/icon.png)

下矿前，在 GTFO 装备大厅对照队友加载的模组版本。每名玩家占一列，同一模组占一行。

## 安装和使用

1. 在模组管理器中安装 `HawChow-conflictmodlist`；手动安装时，把 `conflictmodlist.dll` 放到已安装 BepInEx 的游戏配置的 `BepInEx/plugins/` 目录。
2. 进入装备大厅，在自己的玩家栏点击「模组版本对照」。如果装了 Localia，按钮会在原模组清单按钮上方，原按钮不受影响。
3. 点「关闭」或按 `Esc` 退出表格。大厅光标点在面板右上角的「关闭」按钮上也能关：那里另有一个大厅原生命中区，走大厅自己的光标、射线和按压分发，不依赖系统鼠标。

换用新版时，请先卸载旧包；手动安装的玩家请先移走旧 DLL。要保留原设置，可在退出游戏后将旧配置文件重命名为下文的新文件名。

## 联机怎么看

| 情况 | 表格显示 |
| --- | --- |
| 只有自己安装 | 显示自己的模组版本，队友列标灰「未同步」。 |
| 房主未安装，多名客机已安装 | 已安装的客机可互相查看；房主列标灰「未同步」。 |
| 全队安装并同步 | 对照全队已加载的 BepInEx 模组版本。 |
| 单人 / 只有自己 + BOT | 不做队友比较，直接列本机已加载的插件与版本，标题为「本机模组清单」。 |

已安装的玩家通过 Steam 大厅共享自己的模组名称、GUID 和版本。不读取队友磁盘，也不需要房主转发。「未同步」不等于对方没有安装模组。

## 颜色和筛选

- 绿色为该行已知最高版本，并列都标绿；红色为低于该行已知最高版本。灰色表示未同步、已同步但未报告该模组，或版本未知。
- 默认隐藏全队都加载且版本一致的模组。开启配置后，一致项在表格末尾单独显示。两个分组各自按已知加载人数降序，再按模组名称降序排列。
- 只比较 BepInEx 实际加载的模组，不比较安装包版本、未加载文件或独立 TheArchive 模块。版本号一致不代表 DLL、配置或兼容性一致。
- 表头玩家名优先照大厅原生 TMP 的实际渲染结果画：保留大厅改写后的大小写/缩写和逐字颜色；读不到原生结果时才解析昵称里的颜色标签，最后才用原始昵称。IMGUI 侧不开富文本，名字按「文字 + 颜色」分段绘制，远端昵称不会被当富文本执行；本机列追加的「（你）」是独立一段，不会被名字末尾的颜色染掉。

如需显示一致项，退出游戏后编辑 `BepInEx/config/hawchow.conflictmodlist.cfg`：

```ini
[Display]
ShowMatchingMods = true
```

## 按键

| 按键 | 作用 |
| --- | --- |
| `F` | 在本次会话内切换「差异 / 全部」视图（不写配置文件）。 |
| `↑` / `↓` | 上下滚一行。 |
| `PgUp` / `PgDn` | 上下翻一页。 |
| `Home` / `End` | 跳到清单开头 / 末尾。 |
| `Esc` | 关闭表格。 |

滚动与翻页全部走按键，不假设 IMGUI 鼠标滚轮可用；清单能翻到最后一行。一个插件都没读到（采集为空）与「有一致项但当前被隐藏」（筛选为空）给的是不同提示。

## 反馈与开黑

- 问题反馈与功能建议：[qingju.me](https://qingju.me)
- GTFO 开黑语音频道：[小黑盒开黑频道](https://chat.xiaoheihe.cn/in1wpsjd)

## 源码构建

需要 .NET SDK 7 或更新版本；闸门工程 `tests/FontInteropGate` 目标 `net8.0`，需要 .NET SDK 8 或更新版本。`GTFO_ROOT` 指向已生成 BepInEx 游戏接口程序集的目录：

```bash
dotnet build src/TeamModVersions.csproj -c Release -p:GtfoRoot="$GTFO_ROOT"
dotnet run --project tests/OfflineTests.csproj -c Release
```

本机没有 .NET 7 运行时（只有更新版本）时，用 rollforward 跑离线测试，不必改测试目标框架：

```bash
DOTNET_ROLL_FORWARD=LatestMajor dotnet run --project tests/OfflineTests.csproj -c Release
```

界面字体走 il2cpp interop，改这里容易踩到 JIT/类型加载失败。`tests/FontInteropGate` 用真实 GTFO interop 对生产 DLL 做两道检查：编译产物里 `UnityEngine.Font` 的精确调用点，以及驱动全帧所有调用点的 JIT（只编译不执行）。旧 DLL 应红、修好的 DLL 应绿；IL 解码失败一律判红，不会静默漏扫：

```bash
dotnet run --project tests/FontInteropGate/FontInteropGate.csproj -c Release -- \
  <conflictmodlist.dll> "$GTFO_ROOT/BepInEx/interop" "$GTFO_ROOT/BepInEx/core"
dotnet run --project tests/FontInteropGate/FontInteropGate.csproj -c Release -- \
  --selftest "$GTFO_ROOT/BepInEx/interop" "$GTFO_ROOT/BepInEx/core"
```

闸门工程目标 `net8.0`，需要 .NET SDK 8 或更新版本（与主工程的 net6.0 目标无关）。它只做类型加载/JIT：JIT 通过不等于运行时可调用，native 未实现或运行期抛异常、以及"字体真有中文字形""界面真画出来了"都不在其证明范围内。

注册面和大厅接线各有一道离线闸门。`tests/EntryRegistrationGate` 按 `ClassInjector` 的同一套枚举与过滤规则列出真正会进 il2cpp 元数据的方法，再用真实 `Il2CppClassPointerStore` 检查每个签名能否取到原生 class（取回 `IntPtr.Zero` 即判红）；`tests/LobbyWiringGate` 钉住大厅挂载点与模板来源的调用签名。两者都只做静态检查，不加载 `GameAssembly`、不注册 il2cpp 类型：

```bash
dotnet run --project tests/EntryRegistrationGate/EntryRegistrationGate.csproj -c Release -- \\
  <conflictmodlist.dll> "$GTFO_ROOT/BepInEx/interop" "$GTFO_ROOT/BepInEx/core"
dotnet run --project tests/LobbyWiringGate/LobbyWiringGate.csproj -c Release -- <conflictmodlist.dll>
```

发布包已通过编译与离线测试（`OfflineTests`、`FontInteropGate`、`LobbyWiringGate`、`EntryRegistrationGate`，全部离线、不启动游戏），真实 Steam 联机和游戏内界面尚未实测。
