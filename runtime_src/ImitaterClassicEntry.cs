using System;
using System.Reflection;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「经典模仿者」Mod 的托管运行时入口。
///
/// ── 需求（2026-09-29 用户，最终口径）─────────────────────────────
/// **不改动**游戏内置的模仿者（`PlantLmitater`），**新建**一个植物 `ImitaterClassic`，
/// 行为改成 **PvZ 1 代原版模仿者**：
///   1. 卡牌本身显示阳光为 **0**（或不显示）；
///   2. 点下模仿者时：**已选区（卡槽）** 变成"最后选择的非模仿者植物"；
///      **待选区（卡池）** 仍显示模仿者；若"最后选择的植物"被取消选择，
///      模仿者也跟着回到模仿者本体（不再可选）；
///   3. **种下前**显示为被模仿的植物（不是模仿者）；**种下**时播放模仿者的
///      变身特效；**种下后**变成"被模仿的那张植物"。
///   另有：跨对局不留记录；内部开关默认开（出现在「Mod 植物」页）。
///
/// ── 核心数据源（v1.2.0 修正，这是本次的关键纠正）─────────────────
/// ★★「最后一次选择的植物」= **`TowerDefenseInGameSeedBank.packetList` 里
///    最后一张"非模仿者植物卡"**。
///
/// 为什么必须用它（而不是看卡池卡的 `select`）——读游戏源码后确证：
///   · `TowerDefenseInGameSeedBank.packetList` 是**有序**的：
///       `AddPacket()` 追加（= 玩家"选了"它）、`DeletePacket()` 移除（= 玩家"取消"了它）、
///       `DeleteAllPacket()` 全清。⇒ 列表尾部就是"最后选择的植物"。
///   · 而 `TowerDefenseInGamePacketShow.select` 只是**卡面选中框**的开关，
///     由 `Pressed()` 里的 `select = !select` 翻转；**凡是"不是点出来的"入槽路径都不会碰它**：
///       - `TowerDefenseBattleFeaturePacketBank.PacketListChoose()`（「重新选卡」按钮）
///         → 直接 `PacketChoose(card)` / `CreateAnime()`，**`select` 保持 false**；
///       - `PacketChooseFromName()`（关卡预设卡）→ 直接 `seedBank.AddPacket()`。
///     实测日志里就出现过"卡在卡槽里、但它卡池那张 `select=False`"⇒ 用它记录必然错。
///   · `CreateAnime()` 也是先 `seedBank.AddPacket(Duplicate(config))` 再飞行动画，
///     所以 AddPacket 的时机与玩家的点击一一对应。
///
/// 结论：**以 `packetList` 的顺序为准**。它天然满足用户给的全部用例：
///   选 a → 选 b → 点模仿者  ⇒ 列表 [a, b, 模仿]  ⇒ 尾巴上第一张非模仿者 = b ✔
///   取消 b（点卡池 b 卡）    ⇒ 列表 [a, 模仿]     ⇒ 变成 a ✔（用户步骤 4-5 的 bug 就在这）
///   取消 a                   ⇒ 列表 [模仿]        ⇒ 无被模仿者 ⇒ 还原成模仿者本体 ✔
///
/// ── 换卡实现（要求 3）──────────────────────────────────────────
/// 仍然走原生 `TowerDefenseInGamePacketShow.Cover(cfg, ov, keepColddown, changePacket)`：
///   · `Init(cfg)` 用新 cfg 重算费用/冷却/预览/UI；
///   · `changePacket:true` 会在 `useSucceededActions` 里塞一个
///     `CardActionBehaviorChangePacket { packetConfig = 当前 config }`，
///     该行为在**种成功后**执行 ⇒ 自动还原成模仿者本体（要求 3 的"种完还原"）。
/// 我们**额外**监听这次"还原"（`config.saveKey` 由被模仿植物变回 `MyKey`）
/// 作为"刚刚种下成功"的信号，去补 **模仿者变身特效**
/// （内置 `TowerDefensePlantImitater.Explode()` 里的 `IMITATER_CLOUD` 粒子，
///  uid `djvfnrjg7vtqn`）——因为我们种下的是"被模仿植物本体"，
///  不会走模仿者自己的 `Explode()`，所以那份特效得由 Mod 补上。
///
/// ── 铁律 ───────────────────────────────────────────────────────
/// Initialize / OnAllModsLoaded / Shutdown **一律不许抛**：抛出去 → 整包无条件回滚。
/// 三个回调全部 try/catch。
/// </summary>
public sealed class ImitaterClassicEntry : IXWModRuntimeEntry
{
	private const string P = "[ImitaterClassic] ";

	/// <summary>本 Mod 新增的植物 key（= 场景名 = config.name = packet.saveKey）。</summary>
	private const string MyKey = "ImitaterClassic";

	/// <summary>运行时注册的"只含彩卡植物"的卡池名 —— 新植物场景里的 `packetBank` 指向它。</summary>
	private const string CustomBank = "ImitaterClassicColour";

	/// <summary>彩卡植物的来源卡池 / 分类。</summary>
	private const string SrcBank = "GeneralPlant";
	private const string ColourCat = "Colour";

	/// <summary>内置模仿者变身特效（`TowerDefensePlantImitater.Explode()` 用的那个）。</summary>
	private const string ImitaterCloudUid = "uid://djvfnrjg7vtqn";

	/// <summary>诊断日志开关（走 GD.Print 直出，不受 Mod 日志总开关影响）。</summary>
	private static readonly bool EnableLog = false;

	/// <summary>★ 开关（2026-09-29 用户指定，**默认开启**）：是否让本卡**同时**出现在「Mod 植物」页签。
	///
	/// 「Mod 植物」页签**不是**我们加的，而是 ModLoader 的 `XWModContentCatalog.WithPlants()`
	/// 在**深拷贝卡池时自动塞进去**的（`category["ModPlants"] = 所有 Mod 植物 Packet 的 key`），
	/// 而选卡界面（`SetPacketBankData`）与图鉴（`Almanac`）**都走 WithPlants**
	/// ⇒ 只要卡以 Mod Packet 形式注册，这个页签就必然出现，**无法通过 `mod.json` 关掉**
	/// （不声明 `provides.Packet` 又会让 `TowerDefensePacketConfig.Unlock()` 判定未解锁）。
	/// ⇒ 只能在运行时按本开关处理：
	///   · `true`（默认）  ⇒ 保留在 `ModPlants`，卡在「彩卡」和「Mod 植物」两处都出现；
	///   · `false`         ⇒ 把本卡从 `ModPlants` 摘掉（数组空了就删分类 + 隐藏页签按钮），
	///                       卡**只**出现在「彩卡」页。
	/// 改这个值需要重新编译打包。
	/// </summary>
	private static readonly bool ShowInModPlantsPage = true;

	private SceneTree _tree;
	private Callable _tick;
	private bool _started;
	private bool _bankTried;
	private bool _bankOk;
	private ulong _cloudSceneId;

	/// <summary>"上一次选择的非模仿者植物"（口径见类头注释）。</summary>
	private TowerDefensePacketConfig _lastPlant;

	/// <summary>诊断计数（只打前若干条）。</summary>
	private int _diag;

	// ================================================================ 生命周期

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			string root = (context == null) ? "<null>" : context.PackageRoot;
			Log("初始化完成；PackageRoot=" + root + "。将在选中「经典模仿者」时复制上一次选择的植物种子包。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Initialize 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started)
			{
				return;
			}
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				Log("拿不到 SceneTree，本 Mod 不会生效（游戏其余部分不受影响）。");
				return;
			}
			_tick = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tick);
			_started = true;
			Log("已挂载 process_frame（v1.2.0：改用 seedBank.packetList 顺序判定「上一次选择」）。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void Shutdown()
	{
		try
		{
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tick);
			}
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Shutdown 异常（已吞）：" + ex.Message); } catch { }
		}
		finally
		{
			_started = false;
		}
	}

	// ================================================================ 每帧

	private void OnFrame()
	{
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}
			if (!_bankTried)
			{
				_bankTried = true;
				_bankOk = EnsureColourBank();
			}
			ResetOnLevelChange();
			EnsureCardPlacement();

			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseInGameSeedBank seedBank = mgr.GetSeedBank();

			ResolveLastPlant(seedBank);      // ① 先定"最后一次选择"
			DriveSeedBank(seedBank);         // ② 卡槽里的模仿者卡跟随 ①
			EnforceImitaterSelectable();     // ③ 没有 ① 时卡池的模仿者卡置灰
		}
		catch (Exception ex)
		{
			if (_diag < 200)
			{
				_diag = 200;
				Log("每帧驱动异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	// ================================================================ ① 判定"最后一次选择"

	/// <summary>
	/// ★★ 核心：把 `_lastPlant` 更新成"玩家最后一次选择的非模仿者植物"。
	///
	/// 数据源 = `TowerDefenseInGameSeedBank.packetList` 的**尾部**（该列表按选择顺序追加）。
	/// 战斗期额外用"当前拿在手上准备种的那张卡"（`packetPickControl.packetPick`）覆盖 ——
	/// 因为进关后卡槽是固定的，玩家"再选一次"实际是**点卡槽卡把它拿起来**。
	/// </summary>
	private void ResolveLastPlant(TowerDefenseInGameSeedBank seedBank)
	{
		// ── (a) 战斗期：玩家刚拿起来的卡优先 ────────────────────────
		if (seedBank != null && GodotObject.IsInstanceValid(seedBank) && seedBank.hasGameStarted)
		{
			TowerDefenseInGamePacketShow pick = GetBattleSlotPacket();
			if (pick != null && GodotObject.IsInstanceValid(pick))
			{
				TowerDefensePacketConfig pc = SafeConfig(pick);
				// ★ 排除"我们自己那张模仿者卡"——它此刻可能正显示成被模仿植物
				//   （`config.saveKey` 是别人的 key），只认 `originalSaveKey`。
				bool mine = pick.originalSaveKey == MyKey;
				if (!mine && pc != null && pc.saveKey != MyKey
					&& pc.characterConfig is TowerDefensePlantConfig)
				{
					if (!ReferenceEquals(_battlePick, pc))
					{
						_battlePick = pc;
						Log("战斗期「最后一次选择」= " + pc.saveKey + "（玩家拿起卡槽里的卡）");
					}
				}
			}
		}
		else
		{
			_battlePick = null;
		}

		// ── (b) 卡槽顺序（选卡界面 + 战斗期兜底）────────────────────
		TowerDefensePacketConfig fromBank = null;
		string bankSeq = "";
		if (seedBank != null && GodotObject.IsInstanceValid(seedBank))
		{
			foreach (TowerDefenseInGamePacketShow card in seedBank.packetList)
			{
				if (card == null || !GodotObject.IsInstanceValid(card))
				{
					continue;
				}
				if (card.originalSaveKey == MyKey)
				{
					bankSeq += "[模仿者]";
					continue;      // 模仿者自己不算"被模仿的植物"
				}
				TowerDefensePacketConfig cfg = SafeConfig(card);
				if (cfg == null)
				{
					bankSeq += "[?]";
					continue;
				}
				bankSeq += "[" + cfg.saveKey + "]";
				if (cfg.saveKey == MyKey)
				{
					continue;
				}
				if (cfg.characterConfig is TowerDefensePlantConfig)
				{
					fromBank = cfg;      // 取"最后一张"⇒ 越靠后越新
				}
			}
		}
		if (EnableLog && bankSeq != _lastBankSeq)
		{
			_lastBankSeq = bankSeq;
			Log("卡槽顺序：" + (bankSeq.Length == 0 ? "（空）" : bankSeq));
		}

		TowerDefensePacketConfig resolved = null;
		if (_battlePick != null && GodotObject.IsInstanceValid(_battlePick)
			&& _battlePick.characterConfig is TowerDefensePlantConfig)
		{
			resolved = _battlePick;
		}
		else
		{
			resolved = fromBank;
		}

		if (resolved == null && _lastPlant == null)
		{
			return;
		}
		if (resolved != null && _lastPlant != null && resolved.saveKey == _lastPlant.saveKey)
		{
			return;      // 没变
		}
		string before = _lastPlant == null ? "<无>" : _lastPlant.saveKey;
		string after = resolved == null ? "<无>" : resolved.saveKey;
		_lastPlant = resolved;
		Log("「最后一次选择」更新：" + before + " → " + after);
	}

	/// <summary>战斗期"当前拿起待种"的那张卡（`PacketPickControl.packetPick`）。</summary>
	private TowerDefenseInGamePacketShow GetBattleSlotPacket()
	{
		try
		{
			object mapFeature = TowerDefenseManager.GetMapFeature();
			if (mapFeature == null || !(mapFeature is GodotObject mf) || !GodotObject.IsInstanceValid(mf))
			{
				return null;
			}
			object ppc = GetMember(mapFeature, "packetPickControl");
			if (ppc == null || !(ppc is GodotObject po) || !GodotObject.IsInstanceValid(po))
			{
				return null;
			}
			return GetMember(ppc, "packetPick") as TowerDefenseInGamePacketShow;
		}
		catch
		{
			return null;
		}
	}

	// ================================================================ ② 卡槽跟随

	/// <summary>
	/// 让**卡槽（已选区）**里那张模仿者卡显示成 `_lastPlant`；
	/// 被模仿植物被玩家取消时，把那张模仿者卡**一并移出卡槽**（需求 2b）。
	/// **卡池（待选区）**里那张模仿者卡**一律不动**（用户明确要求"待选区还是显示为模仿者"）。
	/// </summary>
	private void DriveSeedBank(TowerDefenseInGameSeedBank seedBank)
	{
		try
		{
			if (seedBank == null || !GodotObject.IsInstanceValid(seedBank))
			{
				return;
			}

			// ── (1) 前置阶段：移出"被模仿植物已不在卡槽"的模仿者卡（需求 2b）─────
			//   必须独立成一遍：`DeletePacket()` 会改 `packetList`，不能在遍历中调用。
			//   ★ 只在**选卡阶段**做 —— 战斗期卡槽固定、没有"取消选择"，
			//     而且战斗期若那张被模仿植物因 `plantOnce` 被释放（节点失效但仍在
			//     `packetList` 里），`ExistsInBank` 会误判成"不在"⇒ 误删模仿者卡。
			if (seedBank.hasGameStarted != true)
			{
				var toDelete = new System.Collections.Generic.List<TowerDefenseInGamePacketShow>();
				foreach (TowerDefenseInGamePacketShow card in seedBank.packetList)
				{
					if (card == null || !GodotObject.IsInstanceValid(card))
					{
						continue;
					}
					TowerDefensePacketConfig cfg = SafeConfig(card);
					if (cfg == null || cfg.saveKey == MyKey)
					{
						continue;                       // 本体态 / 无 config ⇒ 不删
					}
					if (card.originalSaveKey != MyKey)
					{
						continue;                       // 不是我们的模仿者卡
					}
					if (ExistsInBank(seedBank, cfg.saveKey, card))
					{
						continue;                       // 被模仿植物还在卡槽 ⇒ 保留
					}
					toDelete.Add(card);
				}
				foreach (TowerDefenseInGamePacketShow card in toDelete)
				{
					if (card == null || !GodotObject.IsInstanceValid(card))
					{
						continue;
					}
					TowerDefensePacketConfig cfg = SafeConfig(card);
					Log("被模仿的 " + (cfg != null ? cfg.saveKey : "?") + " 已被取消 ⇒ 模仿者卡一并移出卡槽。");
					_slotShown.Remove(card.GetInstanceId());
					seedBank.DeletePacket(card);
				}
			}

			// ── (2) ★★ v1.2.1 关键修复：**每帧按"身份键"重建 `packetNameSet`**────
			//
			//   `TowerDefenseInGameSeedBank.packetNameSet` 是"卡槽里有哪些卡"的集合，
			//   键 = `config.saveKey`。而游戏自己判定"卡的身份"用的是 **`originalSaveKey`
			//   （非空时）否则 `config.saveKey`**（见 `FindSelectedPacket()` /
			//   `DeletePacket()` / `EmitChooseOverAsync()`）。
			//
			//   我们 `Cover()` 会把卡槽模仿者卡的 `config.saveKey` 改成被模仿植物
			//   ⇒ `AddPacket()` 放进去的 `"ImitaterClassic"` 在 `DeletePacket()` 里
			//     **永远不会被移除**（它 Remove 的是 `"PlantFirenut"` 之类的键）
			//   ⇒ `HasPacket("ImitaterClassic")` 从此恒为 true
			//   ⇒ `BindVirtualizedPacket()` 里 `alive = !HasPacket(saveKey)` 恒 false
			//   ⇒ `PacketChoose()` 的加卡分支 `if (!packet.alive ...) { Reset(); return; }`
			//     **拒绝再把模仿者加进卡槽** ⇒ 用户看到"无法模仿 c / 无法模仿 d"。
			//
			//   ⇒ 按身份键重建，让 `HasPacket()` 与 `FindSelectedPacket()` 口径一致。
			Godot.Collections.Dictionary pns = seedBank.packetNameSet;
			if (pns != null)   // ★ Dictionary 不是 GodotObject，不能走 IsInstanceValid
			{
				pns.Clear();
				foreach (TowerDefenseInGamePacketShow c0 in seedBank.packetList)
				{
					if (c0 == null || !GodotObject.IsInstanceValid(c0))
					{
						continue;
					}
					TowerDefensePacketConfig cf0 = SafeConfig(c0);
					if (cf0 == null)
					{
						continue;
					}
					string idKey = string.IsNullOrEmpty(c0.originalSaveKey) ? cf0.saveKey : c0.originalSaveKey;
					pns[idKey] = true;
				}
			}

			// ── (3) 清理 `_slotShown` 里已不在卡槽的条目 ─────────────────────────
			//   卡槽卡走**对象池**（`ReturnPacketToPool` → `ResetForPool`），
			//   同一个节点会被复用；若不清理，"上一张显示成 X"的旧记录会让
			//   "刚种下成功"的检测误触发（日志里那条
			//   `已补播模仿者变身特效（格子 -1,-1）` 就是这么来的）。
			if (_slotShown.Count > 0)
			{
				var gone = new System.Collections.Generic.List<ulong>();
				foreach (ulong k in _slotShown.Keys)
				{
					bool still = false;
					foreach (TowerDefenseInGamePacketShow c1 in seedBank.packetList)
					{
						if (c1 != null && GodotObject.IsInstanceValid(c1) && c1.GetInstanceId() == k)
						{
							still = true;
							break;
						}
					}
					if (!still)
					{
						gone.Add(k);
					}
				}
				foreach (ulong k in gone)
				{
					_slotShown.Remove(k);
				}
			}

			// ── (4) 主循环：让模仿者卡跟随 `_lastPlant` ───────────────────────────
			foreach (TowerDefenseInGamePacketShow card in seedBank.packetList)
			{
				if (card == null || !GodotObject.IsInstanceValid(card))
				{
					continue;
				}
				TowerDefensePacketConfig cfg = SafeConfig(card);
				if (cfg == null)
				{
					continue;
				}
				bool mine = cfg.saveKey == MyKey || card.originalSaveKey == MyKey;
				if (!mine)
				{
					continue;
				}

				ulong id = card.GetInstanceId();
				string prevShown;
				if (!_slotShown.TryGetValue(id, out prevShown))
				{
					prevShown = null;
				}
				prevShown = prevShown ?? cfg.saveKey;      // 首次见到 ⇒ 以当前状态为准

				if (cfg.saveKey == MyKey)
				{
					// 卡片此刻就是模仿者本体（刚加入卡槽，或刚被游戏的 ChangePacket 还原）。
					string before;
					_slotShown.TryGetValue(id, out before);    // 首次见到 = null
					prevShown = MyKey;
					if (_lastPlant != null && GodotObject.IsInstanceValid(_lastPlant))
					{
						// ★ "刚刚种下成功"的检测：`Cover(..., changePacket:true)` 塞的
						//   `CardActionBehaviorChangePacket` 会在**种成功后**把它 `Init` 回模仿者本体。
						//   ⇒ "上一帧还显示着被模仿植物、这一帧自己变回 MyKey" = 刚种下成功。
						if (!string.IsNullOrEmpty(before) && before != MyKey)
						{
							SpawnImitaterCloud();
						}
						ApplyMimic(card, _lastPlant);
						prevShown = _lastPlant.saveKey;
					}
					_slotShown[id] = prevShown;
					continue;
				}

				// ── 卡片此刻显示成某张"被模仿植物"（cfg.saveKey != MyKey）────────
				if (_lastPlant == null || !GodotObject.IsInstanceValid(_lastPlant))
				{
					RestoreToImitater(card);
					prevShown = MyKey;
				}
				else if (cfg.saveKey != _lastPlant.saveKey)
				{
					ApplyMimic(card, _lastPlant);
					prevShown = _lastPlant.saveKey;
				}
				else
				{
					prevShown = cfg.saveKey;
				}
				_slotShown[id] = prevShown;
			}
		}
		catch (Exception ex)
		{
			if (_diag < 41)
			{
				_diag = 41;
				Log("卡槽跟随异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// 卡槽里是否存在"身份 = `key`"的**其他**卡（用于判断被模仿植物是否还在卡槽里）。
	/// 身份口径与游戏一致：`originalSaveKey` 非空用之，否则用 `config.saveKey`。
	/// </summary>
	private static bool ExistsInBank(TowerDefenseInGameSeedBank seedBank, string key,
		TowerDefenseInGamePacketShow exclude)
	{
		try
		{
			if (string.IsNullOrEmpty(key) || seedBank == null || !GodotObject.IsInstanceValid(seedBank))
			{
				return false;
			}
			ulong exId = (exclude != null && GodotObject.IsInstanceValid(exclude)) ? exclude.GetInstanceId() : 0;
			foreach (TowerDefenseInGamePacketShow c in seedBank.packetList)
			{
				if (c == null || !GodotObject.IsInstanceValid(c))
				{
					continue;
				}
				TowerDefensePacketConfig cf = SafeConfig(c);
				if (cf == null)
				{
					continue;
				}
				string idKey = string.IsNullOrEmpty(c.originalSaveKey) ? cf.saveKey : c.originalSaveKey;
				if (idKey == key && c.GetInstanceId() != exId)
				{
					return true;
				}
			}
		}
		catch { }
		return false;
	}

	/// <summary>卡槽里各模仿者卡"上一次显示成哪张植物"（`saveKey`；用于识别"刚种下成功"）。</summary>
	private readonly System.Collections.Generic.Dictionary<ulong, string> _slotShown
		= new System.Collections.Generic.Dictionary<ulong, string>();

	/// <summary>
	/// 把卡槽里的模仿者卡**换成** `want`（被模仿植物）。
	///
	/// ★ 必须传副本：`Cover()` 会**就地修改**传入的 config
	///   （`_config._override = overrideVal`），传原对象会污染全局共享配置。
	/// ★ 副本**必须保留被模仿植物的 `saveKey`** —— 这是"卡面显示成被模仿植物"的前提：
	///   `TowerDefenseManager.GetPacketSpriteScene()` 优先按 `config.saveKey` 去
	///   `CHARCTAER_SPRITE` 查精灵。
	/// ★ `originalSaveKey` 不变（仍是 `ImitaterClassic`）⇒ `FindSelectedPacket()`
	///   / `DeletePacket()` 依旧把这张卡认作模仿者，卡槽去重、点卡取消都正常。
	///
	/// keepColddown:false ⇒ 用被模仿植物自己的冷却；
	/// changePacket:true ⇒ 种成功后自动还原（要求 3）。
	/// </summary>
	private void ApplyMimic(TowerDefenseInGamePacketShow cur, TowerDefensePacketConfig want)
	{
		try
		{
			TowerDefensePacketConfig cfg = SafeConfig(cur);
			if (cfg == null || want == null || !GodotObject.IsInstanceValid(want))
			{
				return;
			}
			if (cfg.saveKey == want.saveKey)
			{
				return;      // 已经是目标 ⇒ 幂等
			}
			TowerDefensePacketConfig copy = want.Duplicate() as TowerDefensePacketConfig;
			if (copy == null || !GodotObject.IsInstanceValid(copy))
			{
				return;
			}
			copy.@override = null;
			cur.Cover(copy, null, false, true);
			// `Cover → Init` 里有 `if (!_previewCreationDeferred) CreateSprite();`；
			// 若那张卡处于"延迟创建预览"状态，卡面不会重建 ⇒ 再显式调一次（幂等）。
			try { cur.CreateSprite(); } catch { }
			Log("模仿者卡（卡槽）→ " + want.saveKey + "（原=" + cfg.saveKey + "）");
		}
		catch (Exception ex)
		{
			if (_diag < 42)
			{
				_diag = 42;
				Log("换卡异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// 把一张**已被替换过**的模仿者卡**还原成模仿者本体**。
	/// 触发时机：`_lastPlant` 变空（玩家把"最后选择的植物"取消掉了）——
	/// 对应用户要求"如果取消选择最后选择的植物，那么模仿者也要取消选择"。
	/// 实现：用 `originalSaveKey`（替换前就被 `Init` 固化成 `ImitaterClassic`，替换过程中不会变）
	/// 反查回本体 config 再 `Init`，无需额外缓存。
	/// </summary>
	private void RestoreToImitater(TowerDefenseInGamePacketShow cur)
	{
		try
		{
			TowerDefensePacketConfig cfg = SafeConfig(cur);
			if (cfg == null || cfg.saveKey == MyKey)
			{
				return;      // 本来就是模仿者本体 ⇒ 无需还原
			}
			string origKey = cur.originalSaveKey;
			if (string.IsNullOrEmpty(origKey))
			{
				origKey = MyKey;
			}
			TowerDefensePacketConfig orig = TowerDefenseManager.GetPacketConfig(origKey);
			if (orig == null || !GodotObject.IsInstanceValid(orig))
			{
				return;
			}
			TowerDefensePacketConfig copy = orig.Duplicate() as TowerDefensePacketConfig;
			if (copy == null || !GodotObject.IsInstanceValid(copy))
			{
				return;
			}
			copy.@override = null;
			cur.Cover(copy, null, false, true);
			try { cur.CreateSprite(); } catch { }
			Log("模仿者卡已还原（没有可模仿的植物了）→ " + origKey);
		}
		catch { }
	}

	// ================================================================ ③ 卡池置灰

	/// <summary>置灰诊断计数。</summary>
	private int _disableDiag;

	/// <summary>
	/// 用户要求：**还没有选择任何植物时，卡池里的模仿者卡不可选**。
	///
	/// 实现：把卡池那张模仿者卡的 `alive` 置 false。
	/// `TowerDefenseInGamePacketShow.Pressed()` 的守卫是
	///     `else if ((alive || allowPressWhenUnavailable) && !(pressDelayTimer > 0.0))`
	/// ⇒ `alive == false` 时点它**完全没反应**；且 `alive` 的 setter 会调 `ColorSet()`
	///   ⇒ 卡面同时**变灰**，玩家一眼能看出"现在不能选"。
	/// ⚠️ 卡池卡的 `allowPressWhenUnavailable` 被 `BindVirtualizedPacket()` 设成了 `true`
	///    ⇒ 光靠 `alive` 拦不住，所以另外用一个 metadata 标记，并**同时**把
	///    `allowPressWhenUnavailable` 也压掉（恢复时还原）。
	/// ⚠️ 已选中（`select`）或已锁定（`@lock`）的卡**不碰**。
	/// </summary>
	private void EnforceImitaterSelectable()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			bool hasLast = _lastPlant != null && GodotObject.IsInstanceValid(_lastPlant);
			// ★★ v1.3.2 关键修复：**只处理"选卡界面卡池"里那张模仿者卡**。
			//
			//   依据：`TowerDefenseManager.Instance` 只在 `_Ready()` 赋值、**从不清空**
			//   （源码仅一处 `Instance = this`）⇒ 打过一局后，本函数每帧都在全树跑。
			//   而 **图鉴（Almanac）的植物条目复用同一个卡类**
			//   `TowerDefenseInGamePacketShow`（`BindPlantPreview()` 里
			//   `OnPressed += PlantPacketChoose`），图鉴里那张模仿者卡同样
			//   `saveKey == MyKey` 且不在卡槽 ⇒ 上一版把它也 `alive = false` 了
			//   ⇒ `Pressed()` 守卫 `(alive || allowPressWhenUnavailable)` 不成立
			//   ⇒ **图鉴里点这张卡完全没反应，看不到说明**。
			//
			//   ⇒ 改用**祖先链判定**：只有挂在选卡界面 `TowerDefenseInGamePacketBank`
			//     之下的卡才允许置灰；图鉴 / 其他界面一律不碰。
			var cards = new System.Collections.Generic.List<Node>();
			CollectByClassName(_tree != null && GodotObject.IsInstanceValid(_tree) ? _tree.Root : null,
				0, "TowerDefenseInGamePacketShow", cards, 400);
			foreach (Node node in cards)
			{
				TowerDefenseInGamePacketShow p = node as TowerDefenseInGamePacketShow;
				if (p == null || !GodotObject.IsInstanceValid(p))
				{
					continue;
				}
				TowerDefensePacketConfig cfg = SafeConfig(p);
				if (cfg == null || cfg.saveKey != MyKey)
				{
					continue;
				}
				if (!IsInChooseBank(p))
				{
					continue;      // 不是选卡界面卡池的卡（图鉴/卡槽等）⇒ 一律不碰
				}
				if (p.select || p.@lock)
				{
					continue;      // 已选中 / 已锁定 ⇒ 正常状态，不干预
				}
				if (!hasLast)
				{
					if (p.alive)
					{
						p.alive = false;                        // 置灰
						p.SetMeta("ImitaterGreyedByMod", true); // 只标记"我置灰的那张"
						if (_disableDiag < 3)
						{
							_disableDiag++;
							Log("还没有选择任何植物 ⇒ 模仿者置灰、不可选。");
						}
					}
				}
				else if (p.HasMeta("ImitaterGreyedByMod"))
				{
					// ★ 只恢复"我自己置灰的那张"！
					//   若无脑 `if (!p.alive) p.alive = true;` 会把游戏自己因"冷却中"
					//   设成 false 的 `alive` 也强行恢复 ⇒ 直接绕过冷却。
					p.RemoveMeta("ImitaterGreyedByMod");
					p.alive = true;
				}
			}
		}
		catch { }
	}

	/// <summary>
	/// 这张卡是否挂在**选卡界面的卡池**（`TowerDefenseInGamePacketBank`）之下。
	///
	/// `TowerDefenseInGamePacketShow` 是个被到处复用的卡类：选卡界面卡池、战斗卡槽、
	/// **图鉴（Almanac）**、商店等都在用。我们的"没选植物就置灰"只对**选卡界面卡池**
	/// 那张有意义，别的界面碰了就会出事 —— 图鉴里点不动、看不到说明（v1.3.1 实测）。
	/// 判据：祖先链上出现 `TowerDefenseInGamePacketBank`（选卡界面卡池的唯一容器，
	/// 由 `TowerDefenseBattleFeaturePacketBank.Init()` 创建并 `control.AddUI(packetBank, 3)`）。
	/// </summary>
	private static bool IsInChooseBank(Node node)
	{
		try
		{
			for (Node p = node?.GetParent(); p != null; p = p.GetParent())
			{
				if (p.GetType().Name == "TowerDefenseInGamePacketBank")
				{
					return true;
				}
			}
		}
		catch { }
		return false;
	}

	// ================================================================ 模仿者变身特效

	/// <summary>
	/// 补播 **模仿者变身特效**。
	///
	/// 为什么需要补：我们用 `Cover()` 把卡槽那张卡换成了"被模仿植物本体"，
	/// 于是种下时走的是那张植物自己的创建流程，**不会经过
	/// `TowerDefensePlantImitater.Explode()`**，那份"旋转+云"的特效就丢了。
	/// 这里在"种下成功"的信号上手动补一份（与内置 `Explode()` 第一段完全一致）：
	///     `TowerDefenseManager.CreateEffectParticlesOnce(IMITATER_CLOUD, gridPos)`
	/// 位置取**鼠标所在格**（玩家点哪就种哪，所以与落点一致）。
	/// </summary>
	private void SpawnImitaterCloud()
	{
		try
		{
			if (_cloudSceneId == 0)
			{
				PackedScene sc = GD.Load<PackedScene>(ImitaterCloudUid);
				if (sc == null || !GodotObject.IsInstanceValid(sc))
				{
					Log("拿不到模仿者变身影粒子场景（" + ImitaterCloudUid + "），特效跳过。");
					_cloudSceneId = 1;      // 打一次就够，别每帧刷
					return;
				}
				_cloudSceneId = sc.GetInstanceId();
				_cloudScene = sc;
			}
			if (_cloudScene == null || !GodotObject.IsInstanceValid(_cloudScene) || _cloudSceneId == 1)
			{
				return;
			}
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			Viewport vp = mgr.GetViewport();
			if (vp == null || !GodotObject.IsInstanceValid(vp))
			{
				return;
			}
			Vector2I gridPos = mgr.GetMapGridPosFromMouse(vp.GetMousePosition());
			Node effect = TowerDefenseManager.CreateEffectParticlesOnce(_cloudScene, gridPos);
			if (effect == null || !GodotObject.IsInstanceValid(effect))
			{
				return;
			}
			Vector2 pos = TowerDefenseManager.GetMapCellPlantPos(gridPos);
			if (effect is Node2D n2)
			{
				n2.GlobalPosition = pos;
			}
			Node holder = TowerDefenseManager.GetCharacterNode();
			if (holder != null && GodotObject.IsInstanceValid(holder))
			{
				holder.AddChild(effect, false, Node.InternalMode.Disabled);
			}
			Log("已补播模仿者变身特效（格子 " + gridPos.X + "," + gridPos.Y + "）。");
		}
		catch (Exception ex)
		{
			if (_diag < 43)
			{
				_diag = 43;
				Log("补播变身特效失败（本条只报一次）：" + ex.Message);
			}
		}
	}

	private PackedScene _cloudScene;

	// ================================================================ 卡池归置

	/// <summary>卡池归置异常计数。</summary>
	private int _placeDiag;

	/// <summary>
	/// 把本卡放进「彩卡」页，并按 <see cref="ShowInModPlantsPage"/> 处理「Mod 植物」页。
	/// 每帧调用：进关卡时 `packetBankData` 会被 `WithPlants()` 重新构造。
	/// </summary>
	private void EnsureCardPlacement()
	{
		try
		{
			// ── (1) 全局卡池：追加到「彩卡」分类 ─────────────────────
			TowerDefensePacketBankData gp = TowerDefenseManager.GetPacketBankData(SrcBank);
			if (gp != null && GodotObject.IsInstanceValid(gp))
			{
				if (!gp.category.ContainsKey(ColourCat))
				{
					gp.category[ColourCat] = new Godot.Collections.Array();
				}
				Godot.Collections.Array colour = gp.category[ColourCat].AsGodotArray();
				if (colour == null)
				{
					colour = new Godot.Collections.Array();
				}
				if (!colour.Contains(MyKey))
				{
					colour.Add(MyKey);
					gp.category[ColourCat] = colour;
					Log("已把 " + MyKey + " 追加进 " + SrcBank + "." + ColourCat + "（全局卡池）。");
				}
			}

			// ── (2) 关卡内的卡池副本：处理「Mod 植物」分类 ───────────
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseBattleFeaturePacketBank feature = mgr.GetPacketBankFeature();
			if (feature == null || !GodotObject.IsInstanceValid(feature))
			{
				return;
			}
			TowerDefensePacketBankData pb = GetMember(feature, "packetBankData") as TowerDefensePacketBankData;
			if (pb == null || !GodotObject.IsInstanceValid(pb) || !pb.category.ContainsKey("ModPlants"))
			{
				return;
			}
			Godot.Collections.Array modPlants = pb.category["ModPlants"].AsGodotArray();
			if (modPlants == null || !modPlants.Contains(MyKey))
			{
				return;
			}
			if (ShowInModPlantsPage)
			{
				return;      // 开关打开 ⇒ 保留在「Mod 植物」页
			}
			modPlants.Remove(MyKey);
			if (modPlants.Count > 0)
			{
				pb.category["ModPlants"] = modPlants;   // 还有别的 Mod 卡 ⇒ 只摘掉自己
				return;
			}
			pb.category.Remove("ModPlants");
			HideCardModButton(feature);
			Log("已把 " + MyKey + " 从「Mod 植物」页摘掉（只保留「彩卡」页）。");
		}
		catch (Exception ex)
		{
			if (_placeDiag < 3)
			{
				_placeDiag++;
				Log("卡池归置异常（最多 3 条）：" + ex.Message);
			}
		}
	}

	/// <summary>隐藏选卡界面右上角的「Mod 植物」分类按钮（`cardSort/CardMod`）。</summary>
	private void HideCardModButton(TowerDefenseBattleFeaturePacketBank feature)
	{
		try
		{
			object bank = GetMember(feature, "packetBank");
			Node cs = GetMember(bank, "cardSort") as Node;
			if (cs == null || !GodotObject.IsInstanceValid(cs))
			{
				return;
			}
			Control btn = cs.GetNodeOrNull<Control>("CardMod");
			if (btn != null && GodotObject.IsInstanceValid(btn))
			{
				btn.Visible = false;
			}
		}
		catch { }
	}

	// ================================================================ 自定义彩卡池

	/// <summary>
	/// 注册一个"只含彩卡植物"的卡池 `ImitaterClassicColour`。
	///
	/// 新植物场景复用的是内置模仿者的场景/脚本，而 `TowerDefensePlantImitater.Explode()`
	/// 的卡池读取是**硬编码**的 `GetCategory("White") + GetCategory("Original")`
	/// ⇒ 最省事的正解：自定义一个 bank，把它的 White 分类塞成彩卡植物（Original 留空），
	///    再把新植物场景的 `packetBank` 写成 `ImitaterClassicColour`。
	///    这样"随机取卡玩法给到的模仿者，种下随机变成一张彩卡植物"就自动成立。
	/// </summary>
	private bool EnsureColourBank()
	{
		try
		{
			ResourceManager rm = ResourceManager.Instance;
			if (rm == null || !GodotObject.IsInstanceValid(rm))
			{
				return false;
			}
			var banks = rm.TOWERDEFENSE_PACKETBANKS;
			if (banks == null)
			{
				return false;
			}
			if (banks.ContainsKey(CustomBank))
			{
				return true;
			}
			TowerDefensePacketBankData src = TowerDefenseManager.GetPacketBankData(SrcBank);
			if (src == null || !GodotObject.IsInstanceValid(src))
			{
				return false;
			}

			// 收集彩卡分类里的"植物卡"（排除自己，避免随机抽到自己死循环）
			var plants = new Godot.Collections.Array();
			int skipped = 0;
			foreach (Variant v in src.GetCategory(ColourCat))
			{
				string key = v.AsString();
				if (string.IsNullOrEmpty(key) || key == MyKey)
				{
					continue;
				}
				TowerDefensePacketConfig cfg = TowerDefenseManager.GetPacketConfig(key);
				if (cfg == null || !GodotObject.IsInstanceValid(cfg))
				{
					skipped++;
					continue;
				}
				if (!(cfg.characterConfig is TowerDefensePlantConfig))
				{
					skipped++;
					continue;
				}
				plants.Add(key);
			}
			if (plants.Count == 0)
			{
				Log("钻石分类里没有可用的植物卡，随机变身暂不生效。");
				return false;
			}

			TowerDefensePacketBankData data = new TowerDefensePacketBankData();
			data.category["White"] = plants;       // Explode() 抽的就是 White
			data.category["Original"] = new Godot.Collections.Array();   // 留空：不回落到原版卡
			banks[CustomBank] = data;
			Log("已注册卡池 " + CustomBank + "：彩卡植物 " + plants.Count + " 张（跳过 " + skipped + "）。");
			return true;
		}
		catch (Exception ex)
		{
			Log("注册彩卡卡池失败（已吞）：" + ex.Message);
			return false;
		}
	}

	// ================================================================ 换局清理

	/// <summary>关卡指纹（用于检测"换局"，清空跨局残留的记录）。</summary>
	private ulong _levelKey;

	/// <summary>战斗期"玩家刚拿起来待种"的那张卡（`ResolveLastPlant` 的输入）。</summary>
	private TowerDefensePacketConfig _battlePick;

	/// <summary>上次打印的卡槽顺序（变化才打日志）。</summary>
	private string _lastBankSeq;

	/// <summary>
	/// **换局时清空 `_lastPlant` / `_battlePick`**。
	/// `_lastPlant` 是本 Mod 实例的字段，而 Mod 实例**整局游戏只加载一次**
	/// （`OnAllModsLoaded` 只调一次）⇒ 上一局选的植物会**残留到下一局**，
	/// 表现为"游戏还没开始选卡，模仿者就已经变成上一局那张植物了"。
	///
	/// 指纹取 `TowerDefenseManager.currentControl` 的 `GetInstanceId()` ——
	/// 关卡控制节点是**每局重建**的，实例 ID 变化即代表"换了局"。
	/// （不用 `IsInstanceValid(_lastPlant)` 判断：卡片对象可能被对象池复用而不销毁。）
	/// </summary>
	private void ResetOnLevelChange()
	{
		try
		{
			ulong key = 0;
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr != null && GodotObject.IsInstanceValid(mgr))
			{
				TowerDefenseControlNew ctl = mgr.currentControl;
				if (ctl != null && GodotObject.IsInstanceValid(ctl))
				{
					key = ctl.GetInstanceId();
				}
			}
			if (key == _levelKey)
			{
				return;
			}
			bool had = _lastPlant != null || _battlePick != null || _slotShown.Count > 0;
			_levelKey = key;
			_lastPlant = null;
			_battlePick = null;
			_slotShown.Clear();
			_lastBankSeq = null;
			if (had)
			{
				Log("关卡切换 ⇒ 已清空「上一次选择」记录（防止跨局残留）。");
			}
		}
		catch { }
	}

	// ================================================================ 工具

	/// <summary>安全取 config（无效返回 null）。</summary>
	private static TowerDefensePacketConfig SafeConfig(TowerDefenseInGamePacketShow card)
	{
		try
		{
			if (card == null || !GodotObject.IsInstanceValid(card))
			{
				return null;
			}
			TowerDefensePacketConfig cfg = card.config;
			return (cfg != null && GodotObject.IsInstanceValid(cfg)) ? cfg : null;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>按类名递归收集节点（深度优先，最多 max 个）。</summary>
	private static void CollectByClassName(Node node, int depth, string className,
		System.Collections.Generic.List<Node> outList, int max)
	{
		try
		{
			if (node == null || !GodotObject.IsInstanceValid(node) || outList.Count >= max || depth > 40)
			{
				return;
			}
			if (node.GetType().Name == className)
			{
				outList.Add(node);
			}
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				CollectByClassName(node.GetChild(i), depth + 1, className, outList, max);
			}
		}
		catch { }
	}

	/// <summary>反射读字段（含非 public；找不到返回 null）。</summary>
	private static object GetMember(object target, string name)
	{
		if (target == null)
		{
			return null;
		}
		try
		{
			// ★ 逐级遍历基类：C# 的 `Type.GetField(name, NonPublic|Instance)`
			//   **不查基类自己声明的 private 字段**。踩过这个坑：`TowerDefenseSunBase._sprite`。
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				FieldInfo f = t.GetField(name,
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (f != null)
				{
					return f.GetValue(target);
				}
			}
			return null;
		}
		catch
		{
			return null;
		}
	}

	private void Log(string msg)
	{
		if (!EnableLog)
		{
			return;
		}
		try { GD.Print(P + msg); } catch { }
	}
}

// ================================================================ 伴随脚本（CompanionOnly）
//
// ★ 这个类是 Mod 角色 `ImitaterClassic` 的「伴随脚本」实现，**引擎通过反射创建它**：
//   `XWModCharacterCompanionRuntime.TryCreateInstance()`：
//     1. 实例化包内角色场景 → `authoredRoot`
//     2. 读元数据 `mod_character_script_binding = "CompanionOnly"` + `mod_character_script_path`
//     3. `expectedTypeName = Path.GetFileNameWithoutExtension(path)` → `ImitaterClassic`
//     4. 在 **Runtime/ModAssembly.dll** 里找 `!IsAbstract && Name == expectedTypeName
//        && authoredRoot.GetType().IsAssignableFrom(type)` 的类型
//     5. `Activator.CreateInstance` 反射建实例，把壳的**属性 + 子节点**搬过去
//
// ⚠️ **必须继承 `TowerDefensePlantImitater`** —— 包内角色场景 instance 的是
//    `TowerDefensePlantImitater.tscn`，`authoredRoot.GetType()` 正是它；
//    继承别的类会判"基类不兼容"。
// ⚠️ **没有这个类** ⇒ `CreateCharacter("ImitaterClassic")` 失败并**静默回退内置模仿者**。
// ⚠️ 这里**不要** override `_Ready` 等 —— 属性与子节点由引擎拷贝；
//    基类的 `_Ready()` 会从 ComponentSet 取 `ExplodeComponent` 并绑定，行为保持不变。
public partial class ImitaterClassic : TowerDefensePlantImitater
{
}
