# demo-v1 建造/背包扩展（2026-10-07）

完整字段契约沿用 `.workflow/BUILD_DEMO_CONTRACT.md`；消息仍有 type/pv/sid/seq，loopback TCP 与主线程生命周期约束不变。

- inventory_request / inventory_state：36格主背包+4格护甲+副手，rev快照版本；真实 ItemStack 在MC保留，C#只看图标/数量。
- inventory_swap：requestId, rev, fromSlot, toSlot；0..35及40可交换，36..39只读。
- creative_give：requestId,rev,item,slot；实际创造模式及固定7种物品校验。
- build_request：requestId,action,x,y,z；当前选中真实MC方块、距离6格、五种白名单、最多256格；只可拆本会话方块。
- inventory_result / build_result：requestId,ok,message（非空）；普通操作失败不致命。
- block_state：requestId,x,y,z,item；外部改动使用非空稳定原因ID，air表示本会话方块移除。
- player_state增加可选offhandItem/offhandCount；省略时默认空副手。

请求去重保留整个会话，最多4096个结果，满后在变更前非致命拒绝新请求；不能淘汰旧swap导致重复换回。战斗伤害采用独立10秒重试去重，不与背包请求混用。

Java生产序列化fixture在fabric/build/bridge-fixtures.jsonl，由C#桥测试实际读取验证。
这些扩展不含TNT、生物、完整输入/MC屏幕输出；下一协议必须支持独立的伤害来源和实体生命周期，不能伪造attackId接入爆炸。
