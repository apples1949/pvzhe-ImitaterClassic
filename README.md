# 经典模仿者（ImitaterClassic）

> 新增一张**彩卡**植物「经典模仿者」，行为改成 **PvZ 1 代原版模仿者** ——
> 点它 → 变成你**上一次选择的那张植物**，按那张卡的花费与冷却种下。

| 项目 | 内容 |
| --- | --- |
| Mod ID | `imitaterclassic` |
| 程序集 | `JTYImitaterClassic` → 包内 `Runtime/ModAssembly.dll` |
| 入口类 | `ImitaterClassicEntry` |
| 当前版本 | 1.5.0 |
| 分类 | 植物 |
| 归档 | 彩卡 |

## 和游戏内置模仿者的区别（这是本 Mod 的立足点）

| | 内置 `PlantLmitater` | **本 Mod `ImitaterClassic`** |
| --- | --- | --- |
| 抽卡池 | 硬编码 `GetCategory("White")`（白卡） | 运行时注册的 `ImitaterClassicColour`（**只装彩卡**） |
| 卡面阳光 | 原样 | 显示 **0** |
| 游戏自带的那只 | —— | **完全不动**（用户明确要求） |

## 行为口径（读源码核实，不是猜的）

1. 点下模仿者时，**已选区（卡槽）** 变成「最后选择的非模仿者植物」；
   **待选区（卡池）** 仍显示模仿者本体。
2. 若"最后选择的植物"被取消选择，模仿者**跟着回到本体**（不再可选）。
3. **种下前**显示为被模仿的植物（不是模仿者）；**种下**时播模仿者的变身特效；
   **种下后**变成被模仿的那张植物。
4. 跨对局不留记录。

## ★ 核心数据源：为什么必须用 `packetList`（v1.2.0 的关键纠正）

「最后一次选择的植物」= **`TowerDefenseInGameSeedBank.packetList` 里最后一张"非模仿者植物卡"**。

**为什么不能用卡池卡的 `select`** —— 读游戏源码后确证：

| 事实 | 依据 |
| --- | --- |
| `packetList` 是**有序**的 | `AddPacket()` 追加（= 选了）、`DeletePacket()` 移除（= 取消了）、`DeleteAllPacket()` 全清 ⇒ **列表尾部就是"最后选择的植物"**。`CreateAnime()` 也是先 `AddPacket(Duplicate(config))` 再飞行动画，所以 `AddPacket` 的时机与玩家点击**一一对应** |
| `PacketShow.select` 只是**卡面选中框**的开关 | 由 `Pressed()` 里的 `select = !select` 翻转 |
| 凡是**不是点出来**的入槽路径都不碰它 | `PacketListChoose()`（「重新选卡」按钮）→ 直接 `PacketChoose(card)` / `CreateAnime()`，**`select` 保持 false**；`PacketChooseFromName()`（关卡预设卡）→ 直接 `seedBank.AddPacket()` |

实测日志里就出现过"卡在卡槽里、但它卡池那张 `select=False`" ⇒ 用 `select` 记录**必然错**。

`packetList` 天然满足全部用例：

```
选 a → 选 b → 点模仿者   ⇒ 列表 [a, b, 模仿]  ⇒ 尾巴上第一张非模仿者 = b ✔
取消 b（点卡池 b 卡）     ⇒ 列表 [a, 模仿]     ⇒ 变成 a ✔
取消 a                    ⇒ 列表 [模仿]        ⇒ 无被模仿者 ⇒ 还原成本体 ✔
```

## 实现路线：完全复用内置模仿者的艺术与脚本

`build_mod.py` 生成资源，刻意**不复制**任何内置资源：

| 用到的内置资源 | 用途 |
| --- | --- |
| `TowerDefensePlantImitater.tscn` | 角色场景 instance |
| `Imitater.tscn` | 精灵场景 instance |

**唯一的差别**：把场景上的 `packetBank` 指向**运行时注册的自定义卡池** `ImitaterClassicColour`
（由插件启动时注册，只装彩卡植物）⇒ 内置模仿者 `Explode()` 里硬编码的 `GetCategory("White")`
就抽到彩卡植物。

> `CHAR_KEY` 在**四处**必须同名：目录名 / 场景文件名 / `characterConfig.name` / `packet.saveKey`。

## 「Mod 植物」页签（无法通过 mod.json 关掉）

`ShowInModPlantsPage = true`（默认开）控制是否**同时**出现在「Mod 植物」页签。

⚠️ 那个页签**不是我们加的**，而是 ModLoader 的 `XWModContentCatalog.WithPlants()` 在
**深拷贝卡池时自动塞进去**的（`category["ModPlants"] = 所有 Mod 植物 Packet 的 key`），
而选卡界面（`SetPacketBankData`）与图鉴（`Almanac`）**都走 WithPlants**
⇒ 只要卡以 Mod Packet 形式注册，这个页签就**必然出现**，改 `mod.json` 关不掉。

## 目录结构

```
ImitaterClassic/
├── mod.json                          清单（provides: Character / CharacterSprite / Packet）
├── build_mod.py                      生成资源 → 编译 → 打包 → 装机
├── runtime_src/
│   └── ImitaterClassicEntry.cs       入口（约 62 KB，含全部运行时逻辑）
├── Resources/
│   ├── Cards/ImitaterClassic.tres
│   └── Characters/Plants/ImitaterClassic/
│       ├── Config/TowerDefensePlantImitaterClassic.tres
│       ├── Packet/ImitaterClassic.tres
│       ├── Scene/ImitaterClassic.tscn + ImitaterClassicComponentSet.tres
│       └── Sprite/ImitaterClassic.tscn
├── Runtime/ModAssembly.dll           打包用（编译产物改名而来）
└── dist/ImitaterClassic.pmod         成品
```

## 构建

```powershell
python mods\ImitaterClassic\build_mod.py             # 生成资源 + 编译 + 打包
python mods\ImitaterClassic\build_mod.py --install   # 继续装机（覆盖 Mods/*.pmod + 清解包缓存）
```

`build_mod.py` 自动注入本机路径（换机器只改 `mods/_modenv.py`）。

## 硬护栏（违反会整包被拒）

1. `mod.json` 必须在**根**且**唯一**
2. `Runtime/` 下**只允许** `ModAssembly.dll`（多一个可执行文件 → 整包被拒）
3. 包内**绝不允许**出现 `.cs`（`ModLoader.IsExecutablePackageFile` 白名单拒收）

## 已知坑

1. **`packetList` 顺序 ≠ `select` 状态** —— 见上文核心数据源，这是 v1.2.0 修正的根因。
2. **手写 csproj ⇒ 没有 Godot 源码生成器** ⇒ 入口类的自定义 `_Process` / `_Input`
   **引擎根本不会调用**。全部逻辑走 `SceneTree.Connect("process_frame", …)` 信号通道。
   （`Callable.From(new Action(...))` 是唯一正确的挂法。）
3. **`Callable` 不能隐式转 `Action`**（只有反向可以）⇒ 必须 `Connect(名字, callable)`。
4. **`Node.Name` 是 `StringName`** ⇒ 比较必须 `Name.ToString()`。
5. **变身动画需要兜底回收** —— 万一 `OnAnimeCompleted` 没回调，留残影影响体验（见源码的兜底时限常量）。

## 诊断

```csharp
private static readonly bool EnableLog = false;   // 置 true 重新构建即可
```

⚠️ 本 Mod 的诊断日志**直出 `GD.Print`，不受 Mod 日志总开关影响**，排查时记得改回 `false`。

## 版本历史

| 版本 | 变更 |
| --- | --- |
| 1.5.0 | 当前版本 |
| 1.2.0 | ★ 核心数据源纠正为 `packetList`（此前用 `select`，导致取消重选后模仿对象错误） |
| 1.0.0 | 首个版本：新建彩卡植物「经典模仿者」，复用内置模仿者艺术 |
