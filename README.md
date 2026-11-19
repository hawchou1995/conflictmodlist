# conflictmodlist

![conflictmodlist 图标](https://raw.githubusercontent.com/hawchou1995/conflictmodlist/main/packaging/icon.png)

下矿前，在 GTFO 装备大厅对照队友加载的模组版本。每名玩家占一列，同一模组占一行。

## 安装和使用

1. 在模组管理器中安装 `HawChow-conflictmodlist`；手动安装时，把 `conflictmodlist.dll` 放到已安装 BepInEx 的游戏配置的 `BepInEx/plugins/` 目录。
2. 进入装备大厅，在自己的玩家栏点击「模组版本对照」。如果装了 Localia，按钮会在原模组清单按钮上方，原按钮不受影响。
3. 点「关闭」或按 `Esc` 退出表格。

换用新版时，请先卸载旧包；手动安装的玩家请先移走旧 DLL。要保留原设置，可在退出游戏后将旧配置文件重命名为下文的新文件名。

## 联机怎么看

| 情况 | 表格显示 |
| --- | --- |
| 只有自己安装 | 显示自己的模组版本，队友列标灰「未同步」。 |
| 房主未安装，多名客机已安装 | 已安装的客机可互相查看；房主列标灰「未同步」。 |
| 全队安装并同步 | 对照全队已加载的 BepInEx 模组版本。 |

已安装的玩家通过 Steam 大厅共享自己的模组名称、GUID 和版本。不读取队友磁盘，也不需要房主转发。「未同步」不等于对方没有安装模组。

## 颜色和筛选

- 绿色为该行已知最高版本，并列都标绿；红色为低于该行已知最高版本。灰色表示未同步、已同步但未报告该模组，或版本未知。
- 默认隐藏全队都加载且版本一致的模组。开启配置后，一致项在表格末尾单独显示。两个分组各自按已知加载人数降序，再按模组名称降序排列。
- 只比较 BepInEx 实际加载的模组，不比较安装包版本、未加载文件或独立 TheArchive 模块。版本号一致不代表 DLL、配置或兼容性一致。

如需显示一致项，退出游戏后编辑 `BepInEx/config/hawchow.conflictmodlist.cfg`：

```ini
[Display]
ShowMatchingMods = true
```

## 反馈与开黑

- 问题反馈与功能建议：[qingju.me](https://qingju.me)
- GTFO 开黑语音频道：[小黑盒开黑频道](https://chat.xiaoheihe.cn/in1wpsjd)

## 源码构建

需要 .NET SDK 7 或更新版本。`GTFO_ROOT` 指向已生成 BepInEx 游戏接口程序集的目录：

```bash
dotnet build src/TeamModVersions.csproj -c Release -p:GtfoRoot="$GTFO_ROOT"
dotnet run --project tests/OfflineTests.csproj -c Release
```

发布包已通过编译与离线测试，真实 Steam 联机和游戏内界面尚未实测。
