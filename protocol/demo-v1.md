# protocol/demo-v1 — BannerCraft Demo 战斗桥协议(冻结 v1,2026-10-06)

> 本文件是 C#(`src/Mycraft/Bridge/`)与 Fabric(`fabric/`)两端实现的唯一权威。TASK-002 产出;变更须同步两端并更新本文版本号。
> 传输:loopback TCP,**Bannerlord = 监听方(服务端),Minecraft Fabric mod = 客户端**。UTF-8 JSON Lines。

## 1. 传输与地址

| 参数 | 值 | 说明 |
|---|---|---|
| 地址 | `127.0.0.1`(写死,禁止 0.0.0.0/公网) | 端口默认 `38765`,双方配置可覆盖 |
| 帧格式 | 每行一个 JSON 对象 + `\n`,UTF-8 | 无二进制帧 |
| 行上限 | 65536 字节(含 `\n`) | 超限 → 发 `error{code:"overflow"}` 并断开 |
| 消息节奏 | PlayerPose 20 Hz;heartbeat 1 Hz;失联阈值 3 s(按最后收到任意行计) | Demo 初始参数,实测后回填 |
| 单连接 | 服务端同时只接受一个客户端;新连接到来时旧连接保活拒绝握手 | 多人不在范围 |
| 队列 | 收发均有界(入 512 / 出 256);满 → `error{code:"overflow"}` + 断开 | 禁止静默丢命中 |

端口被占用:服务端启动失败,错误必须带 `port_busy` 与端口号,不允许静默降级。

## 2. 公共信封

每行 JSON 必含:`type`(string)、`pv`(int,=1)、`sid`(string,sessionId,握手阶段为 `""`)、`seq`(int,发送方连接内单调递增,从 1 开始)。

- `sessionId`:每次骑砍进入 Demo 战斗重建(GUID 字符串);重连=新 session,**旧 session 消息一律拒绝**(见 §4)。
- `eventId`:命中/受伤事件唯一 ID，接收方去重。2026-10-07 改为 10 秒重试保留窗，覆盖 3×500ms 重试；窗口内不驱逐（C#命中256，Java受伤4096）。近战还必须消耗已签发的 attackId，过期旧命中不能再次应用。突发超过容量安全结束，正常长战斗不再累计至第257次就结束。
- `ack`:处理 `hit_actor`/`player_hurt` 后回 `ack{ackEventId}`；500 ms 间隔最多重试 3 次，每次使用新 seq、相同 eventId；最后 ACK 窗口仍失败则结束会话。seq 在发送队列入口统一分配，防止多线程顺序错乱。

## 3. 消息定义

方向约定:BL→MC / MC→BL。字段均为小写下划线。所有坐标为双精度浮点。

### 握手
| type | 方向 | 字段 | 说明 |
|---|---|---|---|
| `hello` | BL→MC | `agent`,`gameVersion` | 服务端问候;`gameVersion` 如 `v1.5.4.123627` |
| `hello_ack` | MC→BL | `agent`,`mcVersion`,`playerName` | 客户端确认;MC 侧玩家名 |

pv 不符:收到后回 `error{code:"protocol"}` 并断开。

### 会话
| type | 方向 | 字段 | 说明 |
|---|---|---|---|
| `begin_session` | BL→MC | `mode`(固定 `"demo_field"`)、`groundY`、`offsetX`、`offsetZ`、`scale` | 会话建立;坐标变换参数随会话下发(见 §5) |
| `end_session` | BL→MC | `reason`(`"mission_end"|"disconnect"|"error"` | `death` | `resign`) | 骑砍侧会话结束;MC 侧清理代理并停接管 |

### 状态同步
| type | 方向 | 字段 | 说明 |
|---|---|---|---|
| `player_pose` | BL→MC | `x`,`y`,`z`,`yaw` | 骑砍玩家姿态(脚底,度);20 Hz,最新覆盖 |
| `actor_upsert` | BL→MC | `eid`(int)、`x`,`y`,`z`,`yaw`、`alive`(bool)、`name` | 敌人代理创建/更新/死亡标记;同一 session 内 `eid` 稳定 |
| `actor_remove` | BL→MC | `eid` | 会话内移除代理(敌人被清退等) |
| `attack_input` | BL→MC | `attackId`,`x`,`y`,`z`,`yaw` | 近战 AttackRelease 边沿；一次挥击一条，带本次姿态 |
| `select_slot` | BL→MC | `slot`（整数 0–8） | 骑砍滚轮请求真实 MC 选槽；绝对、幂等，MC server 设置后发送选槽包给 MC client，并立即回传 player_state |
| `hit_actor` | MC→BL | `eventId`,`attackId`,`eid`,`damage`(double>0)、`killed`(bool) | MC 射线/原生距离/视线与 Player.attack 命中代理后发出；冷却按原生规则缩放，不承诺冷却内伤害必为零 |
| `player_hurt` | BL→MC | `eventId`,`damage`(double>0)、`sourceEid` | 骑砍敌人命中玩家,转 MC 结算;每次骑砍命中恰好一条 |
| `player_state` | MC→BL | `hp`,`maxHp`,`alive`,`item` | MC 玩家生命/当前主手物品(如 `minecraft:air`);≥1 Hz + 变化即发;`alive=false` 是死亡信号且只发一次 |
| `heartbeat` | 双向 | — | 1 Hz |
| `ack` | 双向 | `ackEventId` | 命中/受伤事件确认 |
| `error` | 双向 | `code`(`"protocol"|"overflow"|"port_busy"|"stale_session"|"session"|"unknown"`)、`message` | 协议不符/溢出 → 断开;`stale_session` 仅提示不断开 |

## 4. 会话与事件规则

1. 未握手(hello/hello_ack 交换前)到达的任何非 `hello` 消息 → `error{protocol}` + 断开。
2. 会话激活后,`sid` ≠ 当前 session 的消息 → `error{stale_session}`,消息丢弃(不断开,防误伤)。
3. `hit_actor`/`player_hurt` 的 `eventId` 已在去重集 → 直接回 `ack`,不重复应用。
4. 队列溢出/协议错误/超时 → 断开并进入 Disconnected;**禁止带着旧伤害继续对战**。
5. MC 对 attackId 去重，BL 只接受本场已发送且未过期的 attackId，每个至多结算一次。攻击匹配有效期 3 秒。MC 确认后立即走 Agent.RegisterBlow 结算；没有排队等下一次原生碰撞或超时补打。重复 eventId 只回 ACK。
6. MC 玩家须为 Overworld 内存活的 Survival 或 Creative 玩家，创造保留免伤；由 connect 命令的 UUID 指定，不自动选择服务器第一个玩家。MC 真暂停菜单断开；失焦暂停设置在桥接期间关闭、结束后恢复。
7. 本次固定地面投影：BL 的水平轴是 x/y、竖直轴 z。MC 收到的 player_pose/actor_upsert 坐标已转换，Java 不再变换一次。

## 5. 坐标变换(Demo 平面映射,C# 与 Java 各实现一份,测试做往返)

```
mcX = blX * scale + offsetX
mcZ = blY * scale + offsetZ
mcY = groundY + (blZ - anchorBLz) * scale
mcYaw = -blYaw + yawOffsetDeg   (yaw 均为度;骑砍 yaw=atan2(dir.x, dir.y)*180/π;MC yaw 0=+Z,顺时针)
```
建造版参数 scale=1.0、yawOffsetDeg=0、groundY=128、水平offset=0。anchorBLz在接入时捕获；DemoTransform仍提供水平/基准变换，Mission适配层叠加高度差。MC暂时noGravity，正常结束恢复；每次姿态拒绝进入非本会话实心方块。用于独立超平坦世界的空中映射区，不是完整地形碰撞镜像。实际原生碰撞仍待实测。

## 6. 样例

BL→MC(一行):
```json
{"type":"player_pose","pv":1,"sid":"a1b2c3d4","seq":42,"x":100.5,"y":73.0,"z":-25.25,"yaw":91.5}
```
MC→BL(一行):
```json
{"type":"hit_actor","pv":1,"sid":"a1b2c3d4","seq":17,"eventId":"e7f1...","attackId":"at9...","eid":1,"damage":5.0,"killed":false}
```

## 7. 参数汇总(Demo 初始值,报告须记录实测)

### HUD 扩展（向后兼容的可选字段，2026-10-06）

player_state 增加 `selectedSlot`（0–8）、`food`（0–20）、`armor`（0–20）及 `slot0Item..slot8Item`（registry StringId）和 `slot0Count..slot8Count`。新接收端若收到 selectedSlot，必须校验全部九格；旧 peer 无该字段时只显示已知的当前主手，不虚构其他物品。air 对应数量 0。

背包/选中槽位/食物/护甲变化最多 5 Hz，HP/死亡变化即时。骑砍滚轮可选槽；数字 1–9 保留战术命令。

### 滚轮输入扩展（2026-10-06）

已新增骑砍滚轮选槽：只在 Active、玩家存活且战场聚焦、非菜单时接管，滚轮向上前一格，向下后一格，首尾循环且包括空格。原生 WieldNextWeapon 仅对当前桥接玩家抑制；离线/其他 Agent 保留原版。

MC 处理 select_slot 在 server thread 执行 Inventory.setSelectedSlot + ClientboundSetHeldSlotPacket + player_state，选中框以确认状态为准；未确认的连续滚轮以本地待确认槽为基准累计，2 秒未确认终止桥接。TCP 绝对选槽无需额外非幂等重试。旧会话、非法范围、非整数与不存活玩家拒绝。不新增数键快捷键，避免占用骑砍战术命令。

`pv=1; port=38765; poseHz=20; heartbeatMs=1000; timeoutMs=3000; maxLineBytes=65536; dedupCapacity=256; retryCount=3; retryIntervalMs=500; attackMatchTtlMs=3000; inQueue=512; outQueue=256`

### 原版交互与界面（2026-10-07，覆盖旧建造模式）

- `interact`：BL→MC，requestId、pressed、target；命中时 x/y/z 为被点击格，face 为 MC Direction 0..5（下/上/北/南/西/东），hitX/Y/Z 为格内命中点，terrain 表示宿主地面代理，canPlace 表示宿主预检查。按住每200ms续租，松开/菜单/失焦发送 pressed=false,target=false。MC 按主手 useOn/use → 副手顺序；useOn 的 SUCCESS/FAIL 都结束。放置限制在 BlockItem.place 内执行，不能阻挡工作台等方块交互。
- `mine`：BL→MC，requestId、pressed、target，命中时 x/y/z/face；只挖本场方块。MC 原版 START/STOP/ABORT 与 destroy progress 处理，超时释放。
- `interaction_result`：MC→BL，requestId、ok、message；普通操作失败不是会话错误。
- player_pose/attack_input 可含 pitch[-90,90]。player_state 的 usingItem 包括双手，usedHand 为 mainhand/offhand/none；mainHandBlock/offHandBlock 来自实际类型；containerOpen 开启边沿让 BL 显示已打开的真实容器。
- block_state 携带真实 block registry ID、stateId、modelKey。空气 modelKey 为空，其余是32位小写hex；本机会话专用文件承载烘焙模型，规格见 TASK-007。MC→BL轴为(x,z,y)，面绕序反转，原版AABB决定碰撞。后台读取/解码，主线程创建引擎资源，尺寸/数量/内存有界，状态变体原子替换。
- 原版界面使用 `%LOCALAPPDATA%/Mycraft/vanilla-ui/<sid>` 原子 input.json/frame.bin；独立token、递增输入seq/确认号、显示帧号防跨菜单误点、尺寸/数量/时效校验。只接管连接中本机玩家当前原版背包/容器，关闭执行原版 onClose 并释放宿主焦点与贴图。
- 旧 inventory_swap/creative_give/build_request/use_item 仅兼容保留；当前UI不调用调试取物或 B 模式。

### TNT / 武器扩展（2026-10-09）

- `player_state`增加weaponKind/offhandWeaponKind（none/melee/bow/crossbow/spear/trident）、useTicks[0,72000]、crossbowCharge[0,1]、crossbowLoaded/Firework、offhandCrossbowLoaded/Firework。usedHand仍来自原版。使用期间状态20Hz；其余沿用既有快照频率。
- `player_pose`、`attack_input`、`actor_upsert`可含完整vx/vy/vz，单位MC blocks/tick，绝对值≤10；宿主速度按20Hz转换(x,z,y)，供原版矛相对速度计算。actor_upsert可含hp/maxHp，经两端伤害倍率反换算供原版非致命毒素规则使用；maxHp≤10000，MC属性上限1024。协议内只传真实宿主血量，不再固定假设目标20HP。
- `interact`可含cancel=true，仅pressed=false允许。真实松开发releaseUsingItem；取消/切槽/菜单/失焦/超时stopUsingItem。不能把取消变成发射/装填提交。
- MC→BL `projectile_state`：entityId、kind（arrow/spectral_arrow/tipped_arrow/trident/firework）、x/y/z、yaw/pitch原生投射物角度、alive；可含原生color[-1,0xffffff]、完整vx/vy/vz（MC blocks/tick，绝对值≤10）和nativeTick。原版飞行/碰撞/弹药为MC权威，宿主只插值显示。每tick最多64状态，静止投射物1Hz保活；玩家使用前保留240投射物门槛及余量，拒绝时保留真实弹药/武器。发射器仅认领本场已登记的真实DISPENSER生成作用域中的无主箭，遵循1024常规实体预算，不套玩家发射门槛。
- MC→BL `weapon_hit`：eventId/sourceId/eid/damage/kind（上述投射物或spear）。原版异步弹道/多目标戳刺/冲击/火焰/附魔伤害不借用近战3秒attackId。独立4096事件预算、10秒去重，既有可靠ACK/3次500ms重试；可信当前会话、有效目标和有限正伤害必需。
- MC→BL `lightning_state`：entityId/x/y/z/seed（16位小写hex，保存全部64bit）/visible/alive。因SummonEntityEffect先添加后setCause，在首个已归属tick登记；visible来自真实life≥0。宿主使用相同Java随机源、原版四层三分支几何，不生成替代闪电时间线。
- MC→BL `player_motion`：spinning、vx/vy/vz原生MC每tick运动、nativeTick≥0。只在激流期间或结束边沿发送；宿主转换速度、三高度场景扫掠及边界裁剪，200ms过期/失焦停止。BL→MC `stop_spin{requestId}`停止原版autoSpin并清空冲量。普通宿主姿态不能清空正在激流的原生delta；激流期间暂时恢复MC原重力标志。
- TNT伤害仅从原版实际捕获回执缩放100/57（26.3 radius4 native最大57），默认宿主倍率1时最大100，保留距离和遮挡。32引线/1024常规实体满时保留原TNT块。投掷物发射前检查容量且不在真实物品已扣除后拒绝；返还失败保留原实体/堆栈。原世界地形和外部实体受保护，包括引雷留下/传播火焰的接触。

- `player_state`可含eyeHeight，有限值(0,4]；缺省兼容1.62。宿主从实际相机准星和MC眼高计算远程方向，按原生速度提前同步飞行路径目标。代理总数64、远程兴趣范围256米保持有界，不修改原版射速/伤害/重力。
- 原版远程tick使用独立、非持久、异步区块票据：最多32中心、96个FULL区块覆盖、64候选投射物。真实当前与未来8tick扫掠范围供ENTITY_TICKING；等待新覆盖就绪期间保留仍有效旧覆盖。静止/回收/退役/会话退出撤销，实体仍由原版tick一次，不同步生成远处区块或手动补tick。

有限支撑/建造镜像仍不等于完整宿主地形镜像；完整双游戏输入、复杂场景碰撞和激流实际运动另做实战验收。无战役存档/多人协议改动，pv仍为1，两端必须同时更新。
