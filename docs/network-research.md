# 大厅网络分析：指标来自哪里

决策：显示谁观察了哪条已有连接，而不是给每位玩家打一个看似精确的“网络分”。大厅常显、不需点按钮；不发送游戏操作作为探针。

| 原始资料 | 作者实现或指标定义 | 在本模组怎么用 / 为什么不照搬 |
|---|---|---|
| [Valve GameNetworkingSockets：SteamNetConnectionRealTimeStatus_t](https://github.com/ValveSoftware/GameNetworkingSockets/blob/master/include/steam/steamnetworkingtypes.h)、[Steam 会话查询](https://partner.steamgames.com/doc/api/ISteamNetworkingMessages#GetSessionConnectionInfo) | 状态、当前 ping（ms）、双方观测的交付质量、近期实际收发字节/秒是不同的字段。本端质量包含“按序”因素，估计可发送速率也不是实际吞吐。 | 优先读已有 Steam 链路。不把 `1−质量` 宣称为精确丢包率，不把不存在的会话算成 0 ms。收发只指游戏连接，不是整机外网测速。 |
| [Valve 大厅成员数据](https://partner.steamgames.com/doc/api/ISteamMatchmaking#SetLobbyMemberData) | 每名用户写自己的成员元数据，大厅成员能读取；不同于只有房主能写的大厅公共字段。 | 用独立键低频互报，保留测量端、目标和有效期。共享的是已有测量，不是对没装插件的人发探测。 |
| [W3C WebRTC Stats](https://www.w3.org/TR/webrtc-stats/) | `candidate-pair.currentRoundTripTime`、本地接收统计、远端报告各有观测对象；RTP 的 `jitter` 是包到达间隔的统计，而非 ping 差。 | 采纳来源和链路分列。两个客机各自到房主的 RTT 不足以推算客机之间的 RTT；也不引入 WebRTC 或采集地址。 |
| [ENet：处理 ACK 的源码](https://github.com/lsalzman/enet/blob/master/protocol.c) | `enet_protocol_handle_acknowledge` 分别更新平滑 RTT 和 RTT 波动。 | 至少两个连续有效 RTT 样本才谈波动；实际输入是周期性 Steam 快照，不套用 ENet 的逐 ACK 算法，也不把快照差叫单向逐包抖动。 |
| [Hikaria / NetworkQualityTracker](https://thunderstore.io/c/gtfo/p/Hikaria/NetworkQualityTracker/)、[作者源码](https://github.com/Tuesday1028/GTFO_NetworkQualityTracker) | 作者区分到本机、到房主；其到房主的连接质量需要房主安装。源码把心跳发送和 UI 刷新分开。 | 采用分来源和低频刷新，不采用“必须有房主插件”的基础路径。 |
| [randomuserfood / Netstat](https://thunderstore.io/c/gtfo/p/randomuserfood/Netstat/) | 作者的 RTT 图测本机到房主；默认混合模式在局内用 NavMarker，在大厅用 Chat，Rx/Tx 单列。 | 借鉴拆分 RTT 与吞吐；不复用聊天或地图标记，避免影响未安装的房主和正常操作。 |
| [Localia / ModList](https://thunderstore.io/c/gtfo/p/Localia/ModList/) | 大厅并列展示模组和延迟；页面明确提到双方装 Core。页面未公开具体 RTT 算法。 | 延续熟悉的大厅场景与可滚动清单；不推定它能测任意两名队友，不依赖 LocaliaCore。 |
| [Valheim / PingHud（Thunderstore）](https://thunderstore.io/c/valheim/p/ShadowRiver/PingHud/) | 作者区分连接类型不支持与尚未出数；房主数值是多连接平均，还有独立收发/抖动行。 | 保留明确缺失状态和读数单位；不把平均值当作某位队友的链路。 |
| [Helldivers 2 / HD2-Ping-Overlay（Nexus）](https://www.nexusmods.com/helldivers2/mods/17434) | 房主能看多个队友，客机显示到房主；作者的常显层按角色呈现，可隐藏。 | 借鉴自动显示和视角说明。不能观测的客机间路径不外推；不依据单次固定阈值给玩家定性。 |

以上是接口、源代码与作者发布说明，不是这些游戏或本 DLL 的实机测量。执行环境、刷新周期、快照波动公式和预算的实际值见本项目验收记录。