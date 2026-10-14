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
	// 赞助码注入器（SponsorQrInjector.cs 共享组件）用的包根缓存
	internal static string SponsorQrRoot = null;
	private bool _settingsSubscribedIc;

	private void OnRuntimeSettingsChanged(object keys)
	{
		try { OnRuntimeSettingsChangedImpl(keys); } catch { }
	}

	private void OnRuntimeSettingsChangedImpl(object keys)
	{
		try { ReadMultiCopySetting(_context); } catch { }
		try { ReadSunCostSettings(_context); } catch { }
		Log("配置已重读（多重复制/阳光消耗）。");
	}


	private const string P = "[ImitaterClassic] ";

	// ---- P2-9：复制是否增加阳光消耗 + 倍数（用户要求）----
		private const string KAddSunCost = "addSunCost";
		private const string KMultiCopy = "allowMultipleCopy";
		/// <summary>允许「多次复制」：模仿者卡**不再跟着被模仿植物被移出卡槽**（默认关 = 保持原行为）。</summary>
		private XWModRuntimeContext _context;
	private bool _settingsSubscribedIcEntry;

	private void OnEntrySettingsChanged()
	{
		try
		{
			ReadMultiCopySetting(_context);
			ReadSunCostSettings(_context);
			ReadGreySettings(_context);
			Log("配置已重读。");
		}
		catch { }
	}

	private static bool _allowMultipleCopy;
	private readonly System.Collections.Generic.Dictionary<ulong, bool> _lastAliveSeen = new System.Collections.Generic.Dictionary<ulong, bool>();
		private const string KSunMul = "sunCostMultiplier";
		private static bool _addSunCost;
		private static int _sunMul = 1;

	// ---- v1.7.0：两处"变灰"开关（用户要求）----
	/// <summary>卡面显示"被模仿的植物"时，是否把卡面里的植物**变灰**（默认开）。</summary>
	private static bool _greyMimicCard = true;
	/// <summary>模仿者变身出来的那株植物是否**变灰**（默认开＝游戏内置模仿者的表现）。</summary>
	private static bool _greyMimicPlant = true;
	private const string KGreyCard = "greyMimicCard";
	private const string KGreyPlant = "greyMimicPlant";

	/// <summary>本 Mod 新增的植物 key（= 场景名 = config.name = packet.saveKey）。</summary>
	private const string MyKey = "ImitaterClassic";

	/// <summary>运行时注册的"只含彩卡植物"的卡池名 —— 新植物场景里的 `packetBank` 指向它。</summary>
	private const string CustomBank = "ImitaterClassicColour";

	/// <summary>彩卡植物的来源卡池 / 分类。</summary>
	private const string SrcBank = "GeneralPlant";
	private const string ColourCat = "Colour";

	/// <summary>内置模仿者变身特效（`TowerDefensePlantImitater.Explode()` 用的那个）。</summary>
	private const string ImitaterCloudUid = "uid://djvfnrjg7vtqn";

	/// <summary>内置模仿者的**精灵场景**（要补播的变身动画就在它上面）。</summary>
	private const string ImitaterSpritePath =
		"res://Asset/Anime/Character/Plant/Chapter0/Imitater/Imitater.tscn";

	/// <summary>
	/// ★ v1.3.5：内置 `TowerDefensePlantImitaterExplodeDefinition.tres` 的
	/// `explodeAnimeClips = "Explode"` + `explodeAnimeTimeScale = 0.5`
	/// （`ExplodeComponent` 进入爆炸态时 `sprite.SetAnimation("Explode", loop:false, 0.2)`）。
	/// 精灵上有 `Imitater_spin` 图层 —— 那段"旋转变身"就是它。
	/// </summary>
	private const string ImitaterExplodeClip = "Explode";
	private const double ImitaterExplodeTimeScale = 0.5;

	/// <summary>变身动画兜底回收时间（秒）——万一 `OnAnimeCompleted` 没回调也不留残影。</summary>
	private const double ImitaterTransformFallbackSec = 2.2;

	/// <summary>诊断日志开关（走 GD.Print 直出，不受 Mod 日志总开关影响）。</summary>
	private static bool EnableLog;   // 由 Initialize 从「Mod 配置」读取（原为编译期常量）

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

	/// <summary>"上一次选择的非模仿者植物"（口径见类头注释）。</summary>
	private TowerDefensePacketConfig _lastPlant;

	/// <summary>
	/// ★★ v1.5.0：**卡槽里每一张模仿者卡各自要跟随哪一张植物**（键 = 卡实例 ID）。
	///
	/// 依据（原版 PvZ1 语义）：模仿者复制的是**它前面那一张卡**。用户的自制关卡
	/// 用「预选卡模式」把卡组写成实卡/模仿者交替：
	///     热狗 · 模仿者 · 豌豆炮 · 模仿者 · 加农炮 · 模仿者 · 冰炮 · 模仿者 · 末日炮 · 模仿者
	/// 若全部按"最后一次选择"跟随，5 张模仿者会**全变成同一张**（列表里最后一张实卡）
	/// ⇒ 交替设计的意图完全看不出来（用户反馈"选择了多个模仿者 会有显示异常"）。
	/// 该表每帧在 <see cref="DriveSeedBank"/> 里重建。
	/// </summary>
	private readonly System.Collections.Generic.Dictionary<ulong, TowerDefensePacketConfig> _bankTargets
		= new System.Collections.Generic.Dictionary<ulong, TowerDefensePacketConfig>();

	/// <summary>
	/// 变身目标**锁存**：玩家拿起某张模仿者卡时按它的左邻锁定，放下后**仍保持**。
	/// 原因：`TowerDefensePlantImitater.Explode()` 在种下之后（旋转动画播完）才跑，
	/// 那时手上已经没有卡了；只有锁存才能保证"种哪张变哪张"。
	/// </summary>
	private TowerDefensePacketConfig _latchedTarget;

	/// <summary>诊断计数（只打前若干条）。</summary>
	private int _diag;

	/// <summary>v1.7.0「同一张植物只能被一张模仿卡模仿」的诊断计数。</summary>
	private int _dupDiag;

	// ================================================================ 生命周期

	
	// ============================================================ 配置面板接线
	//
	// mod.json 里声明了 enableLog（bool，默认 false）。
	// 这里在 Initialize 读一次 —— 老宿主没有 context.Settings 时吞异常、按 false 处理。
	// 用 Get<object> 而不是 Get<bool>：宿主把值存成 Dictionary<string, object>，
	// 直接 Get<bool> 可能抛 InvalidCastException。

	private static bool ReadLogSetting(XWModRuntimeContext context)
	{
		try
		{
			if (context == null)
			{
				return false;
			}
			object v = context.Settings.Get<object>("enableLog");
			if (v is bool b)
			{
				return b;
			}
			if (v is string s && bool.TryParse(s, out bool sb))
			{
				return sb;
			}
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { return true; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.False) { return false; }
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

public void Initialize(XWModRuntimeContext context)
	{
			try { SponsorQrRoot = context.PackageRoot; } catch { }
			_context = context;
			try
			{
				if (context?.Settings != null && !_settingsSubscribedIcEntry)
				{
					context.Settings.Changed += _ => OnEntrySettingsChanged();
					_settingsSubscribedIcEntry = true;
				}
			}
			catch { }
			ReadSunCostSettings(context);   // P2-9：复制加阳光消耗 + 倍数
			ReadMultiCopySetting(context);   // P2-9：是否允许多次复制
			ReadGreySettings(context);       // v1.7.0：卡面变灰 / 复制出的植物变灰

			EnableLog = false;   // ★ 2026-10-14 用户要求：默认关闭所有日志（原：EnableLog = ReadLogSetting(context)）

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
		try { SponsorQrInjector.Tick(Engine.GetMainLoop() as SceneTree, "经典模仿者", SponsorQrRoot, "Resources/SponsorQR.png"); } catch { }
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
			EnsureEditorBank();

			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseInGameSeedBank seedBank = mgr.GetSeedBank();

			ResolveLastPlant(seedBank);        // ① 全局兜底目标
			DriveSeedBank(seedBank);           // ② 卡槽：**逐张各自跟随左邻实卡**（内部建 _bankTargets）
			EnforceMimicPlantGrayscale();      // ★ v1.7.0：按配置决定"变身出来的植物"要不要变灰
			PointColourBankAtHeld();           // ②b 变身目标 = 当前拿起那张模仿者的目标（锁存）
			EnforceImitaterSelectable();       // ③ 没有 ① 时卡池的模仿者卡置灰 + 维护"点一次加一张"的按压语义
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

			// ── (1) 前置阶段：被模仿植物已不在卡槽 ⇒ 模仿者卡一并移出（需求 2b）──
			//   ★ v1.4.0：判断"这张模仿者卡当前显示成哪张植物"改看 `_slotShown`
			//   （不再看 `config.saveKey` —— 现在不改 config 了，见 ReskinCard 说明）。
			//   必须独立成一遍：`DeletePacket()` 会改 `packetList`，不能在遍历中调用。
			// ★ P2-9①：开了「多次复制」就不再跟随移除（模仿者卡可留着重用）
			if (seedBank.hasGameStarted != true && !_allowMultipleCopy)
			{
				var toDelete = new System.Collections.Generic.List<TowerDefenseInGamePacketShow>();
				foreach (TowerDefenseInGamePacketShow card in seedBank.packetList)
				{
					if (card == null || !GodotObject.IsInstanceValid(card))
					{
						continue;
					}
					if (card.originalSaveKey != MyKey)
					{
						continue;                       // 不是我们的模仿者卡
					}
					if (!_slotShown.TryGetValue(card.GetInstanceId(), out string shown)
						|| string.IsNullOrEmpty(shown) || shown == MyKey)
					{
						continue;                       // 显示的是模仿者本体 ⇒ 不管
					}
					if (ExistsInBank(seedBank, shown, card))
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
					Log("被模仿的 " + _slotShown[card.GetInstanceId()] + " 已被取消 ⇒ 模仿者卡一并移出卡槽。");
					_slotShown.Remove(card.GetInstanceId());
					_slotSprite.Remove(card.GetInstanceId());
					seedBank.DeletePacket(card);
				}
			}

			// ── (2) ★★ v1.2.1 关键修复：按"身份键"重建 `packetNameSet` ──
			//   （`Cover()` 已不再使用，但这条校准仍然必要：它保证 `HasPacket()` 与
			//     `FindSelectedPacket()` 口径一致 —— 详见 v1.2.1 注释。）
			Godot.Collections.Dictionary pns = seedBank.packetNameSet;
			if (pns != null)
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

			// ── (3) 清理已不在卡槽的记录（卡走对象池，节点会被复用）──
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
					_slotSprite.Remove(k);
				}
			}

			// ── (4a) ★★ v1.5.0：**逐张解析"各自跟随哪一张"** ──────────────
			//   规则（原版 PvZ1 语义）：一张模仿者卡跟随**它前面最近的那张植物卡**。
			//   取不到（模仿者排在第一位 / 前面没有实卡）才回退到全局 `_lastPlant`。
			//   依据：`seedBank.packetList` 就是"卡槽里的卡的顺序" ——
			//     · 选卡模式：按玩家选择的先后追加；
			//     · 预选卡模式（PRESET）：`TowerDefenseBattleFeaturePacketBank.PacketBankInit()`
			//       按关卡 `config.packetList` 的顺序逐张 `seedBank.AddPacket()` 追加。
			_bankTargets.Clear();
			TowerDefensePacketConfig prevPlant = null;
			foreach (TowerDefenseInGamePacketShow c2 in seedBank.packetList)
			{
				if (c2 == null || !GodotObject.IsInstanceValid(c2))
				{
					continue;
				}
				if (c2.originalSaveKey == MyKey)
				{
					_bankTargets[c2.GetInstanceId()] = prevPlant;   // 左邻实卡；没有 ⇒ null
					continue;
				}
				TowerDefensePacketConfig cf2 = SafeConfig(c2);
				if (cf2 != null && cf2.saveKey != MyKey && cf2.characterConfig is TowerDefensePlantConfig)
				{
					prevPlant = cf2;         // 越靠后越新
				}
			}

			// ── (4b) 主循环：每张模仿者卡按**它自己的目标**换卡面 ────────
			//   ★ v1.7.0：同时执行「同一张植物只能被一张模仿卡模仿」——
			//   按**卡槽顺序从左到右先到先得**；抢不到唯一目标的那张卡会被移出卡槽
			//   （收集到 `dupDrop`，循环结束后统一删，避免边遍历边改 `packetList`）。
			var claimedTargets = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
			var dupDrop = new System.Collections.Generic.List<TowerDefenseInGamePacketShow>();
			foreach (TowerDefenseInGamePacketShow card in seedBank.packetList)
			{
				if (card == null || !GodotObject.IsInstanceValid(card))
				{
					continue;
				}
				if (card.originalSaveKey != MyKey)
				{
					continue;                 // 只管我们的模仿者卡
				}
				TowerDefensePacketConfig want = TargetOfCard(card);
				// ★ v1.7.0：同一张植物不能被两张模仿卡同时模仿（先到先得）
				if (want != null && GodotObject.IsInstanceValid(want) && !string.IsNullOrEmpty(want.saveKey)
					&& !claimedTargets.Add(want.saveKey))
				{
					dupDrop.Add(card);
					if (_dupDiag < 20)
					{
						_dupDiag++;
						Log("同一张植物只能被一张模仿卡模仿：「" + want.saveKey
							+ "」已被前面的模仿卡占用 ⇒ 这张多余的模仿卡移出卡槽。");
					}
					continue;
				}
				string desired = (want == null) ? MyKey : want.saveKey;
				ulong id = card.GetInstanceId();
				bool needReskin = !_slotShown.TryGetValue(id, out string shown) || shown != desired;
				if (!needReskin && _slotSprite.TryGetValue(id, out ulong prevSpr))
				{
					// 卡面若被游戏自己重建过（精灵对象换了）⇒ 也要重刷一次
					ulong nowSpr = SpriteIdOf(card);
					if (nowSpr != 0 && nowSpr != prevSpr)
					{
						needReskin = true;
					}
				}
				if (needReskin)
				{
					ReskinCard(card, want);
					_slotShown[id] = desired;
					_slotSprite[id] = SpriteIdOf(card);
					Log("卡槽模仿者卡面 → " + (want == null ? "模仿者本体" : want.saveKey));
				}
				// ★ v1.4.4：价格每帧跟随（涨价植物 / 关卡改价都能同步）
				SyncMimicCost(card, want);
				// ★ v1.6.1（用户要求）：卡面显示的是**被模仿的植物**时把卡面植物**变灰**
				//   ⇒ 一眼区分"这是模仿卡"和"这是真的那张卡"；还原成模仿者本体时取消灰度。
				//   放在 ReskinCard 之后的**同一帧**执行（`needReskin` 时也不会闪一帧原色）。
				//   v1.7.0：受配置项「模仿者卡面变灰」控制。
				ApplyMimicGrayscale(card, _greyMimicCard && want != null);
				// ★ P2-9①落地：开了「允许多次复制」⇒ 模仿者卡种植后进 CD/变灰时
				//   立即恢复可用（Reset + alive=true）——真正的"反复使用"。
				if (_allowMultipleCopy && seedBank.hasGameStarted == true)
				{
					// 诊断：状态变化时打印（避免刷屏）
					bool wasAlive = _lastAliveSeen.TryGetValue(id, out bool w) ? w : true;
					if (wasAlive != card.alive)
					{
						Log("多次复制跟踪：模仿者卡 alive " + wasAlive + " → " + card.alive
							+ (card.alive ? "" : " ⇒ 即将重置"));
						_lastAliveSeen[id] = card.alive;
					}
					if (!card.alive)
					{
						card.alive = true;
						card.Reset();
						_lastAliveSeen[id] = true;
						Log("多次复制：模仿者卡已重置可用。");
					}
				}
			}

			// ── (4c) ★ v1.7.0：把"抢不到唯一模仿目标"的多余模仿卡移出卡槽 ──
			//   （必须在循环外删：`DeletePacket` 会改 `packetList`）
			foreach (TowerDefenseInGamePacketShow dup in dupDrop)
			{
				if (dup == null || !GodotObject.IsInstanceValid(dup))
				{
					continue;
				}
				ulong dupId = dup.GetInstanceId();
				_slotShown.Remove(dupId);
				_slotSprite.Remove(dupId);
				seedBank.DeletePacket(dup);
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

	/// <summary>取卡面精灵对象的实例 ID（0 = 没有/拿不到）。</summary>
	private static ulong SpriteIdOf(TowerDefenseInGamePacketShow card)
	{
		try
		{
			object sp = GetMember(card, "sprite");
			return (sp is GodotObject go && GodotObject.IsInstanceValid(go)) ? go.GetInstanceId() : 0UL;
		}
		catch
		{
			return 0UL;
		}
	}

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

	/// <summary>卡槽里各模仿者卡"当前显示成哪张植物"（`saveKey`；`MyKey` = 模仿者本体）。</summary>
	private readonly System.Collections.Generic.Dictionary<ulong, string> _slotShown
		= new System.Collections.Generic.Dictionary<ulong, string>();

	/// <summary>各模仿者卡上次换面后的精灵对象 ID（用于发现"卡面被游戏重建过"）。</summary>
	private readonly System.Collections.Generic.Dictionary<ulong, ulong> _slotSprite
		= new System.Collections.Generic.Dictionary<ulong, ulong>();

	/// <summary>
	/// ★★ v1.4.0 架构调整：**不再用 `Cover()` 改 config**，只换**卡面精灵**。
	///
	/// ── 为什么必须改 ────────────────────────────────────────────────
	/// 旧做法 `Cover(copy)` 会把卡槽卡的 `config.saveKey` 换成被模仿植物 ⇒ 种下时
	/// `Plant()` 创建的是"被模仿植物本体"，**整个绕过模仿者角色** ⇒
	/// 内置模仿者那段 `"Explode"` 旋转变身动画（`Imitater.tres` 片段 50~80 帧，
	/// `explodeAnimeTimeScale = 0.5`）**永远不会播**。
	/// 用户明确要看："我要看到模仿者旋转的动画"。
	///
	/// ── 现在 ────────────────────────────────────────────────────────
	/// `config` 保持**模仿者本体**（`saveKey` 恒为 `ImitaterClassic`）⇒ 种下创建
	/// `ImitaterClassic` 角色（= `TowerDefensePlantImitater` 的伴随脚本）⇒
	/// 游戏自己的 `ExplodeComponent`：
	///   ① 播 `"Explode"` 片段（旋转，0.5 倍速）
	///   ② `OnAnimeCompleted` → `InvokeExplodeCallbacks()` → `TowerDefensePlantImitater.Explode()`
	///      → 放云特效 + 按 `packetBank`（= `ImitaterClassicDiamond`）随机生成植物
	/// 目标植物由 <see cref="PointColourBankAt"/> 把该卡池收窄成"只有那一张"来决定 ⇒ 结果确定 ✓
	///
	/// ── 卡面怎么换 ──────────────────────────────────────────────────
	/// 临时把 `ResourceManager.CHARCTAER_SPRITE[MyKey]` 指向目标植物的精灵场景，
	/// 调 `card.CreateSprite()` 重建卡面，**随后立刻还原字典** —— 卡池那张卡（已有自己的精灵）
	/// 与别处都不受影响。
	///
	/// ⚠️ 试过"单独实例化 `Imitater.tscn` 播动画"这条路（v1.3.5）：**不渲染** ——
	///    `AdobeAnimateSprite` 必须由游戏的渲染管线接管，野路子实例化画不出来。
	/// </summary>
	private void ReskinCard(TowerDefenseInGamePacketShow card, TowerDefensePacketConfig want)
	{
		try
		{
			ResourceManager rm = ResourceManager.Instance;
			if (rm == null || !GodotObject.IsInstanceValid(rm))
			{
				return;
			}
			var dict = rm.CHARCTAER_SPRITE;
			if (dict == null)
			{
				return;
			}

			// 目标精灵：被模仿植物的卡面（按 saveKey 优先，回落角色名）
			Resource targetScene = null;
			if (want != null)
			{
				if (!string.IsNullOrEmpty(want.saveKey) && dict.ContainsKey(want.saveKey))
				{
					targetScene = dict[want.saveKey];
				}
				else if (want.characterConfig != null)
				{
					targetScene = rm.GetCharacterSprite(want.characterConfig.name);
				}
			}
			else
			{
				// want == null ⇒ 换回模仿者自己的精灵
				if (dict.ContainsKey(MyKey))
				{
					targetScene = dict[MyKey];
				}
				else
				{
					TowerDefensePacketConfig self = TowerDefenseManager.GetPacketConfig(MyKey);
					if (self != null && GodotObject.IsInstanceValid(self) && self.characterConfig != null)
					{
						targetScene = rm.GetCharacterSprite(self.characterConfig.name);
					}
				}
			}
			if (targetScene == null || !GodotObject.IsInstanceValid(targetScene))
			{
				return;              // 拿不到就不动，保持原样
			}

			// ── 先把"卡面参数"准备好（必须在**字典替换的窗口内**重建精灵）──
			//   ★★ v1.4.2 修正：v1.4.1 把 `Init()+CreateSprite()` 放在了**还原字典之后**
			//   ⇒ 精灵被按"模仿者自己"又重建了一次 ⇒ 卡面回到灰精灵、而费用/背景已经是
			//   被模仿植物的（用户截图就是这个症状）。
			//   `CreateSprite()` 是同步的：`sprite = GetPacketSprite(config)`
			//   → 读 `CHARCTAER_SPRITE[config.saveKey]`（saveKey 恒为 MyKey）
			//   ⇒ **只能在字典被换掉的窗口内调用**。
			TowerDefensePacketConfig cfg = SafeConfig(card);
			TowerDefensePacketConfig refCfg = (want != null)
				? want
				: TowerDefenseManager.GetPacketConfig(MyKey);
			if (cfg != null)
			{
				// ① 背景（稀有度）+ 阳光数量 + 「+」号：全部走 `_override`
				//    （`_GetType()` → `_override.type`；`GetCost()` → `_override.cost`；
				//      `GetCostRise()` → `_override.costRise`（-1 = 不显示「+」））
				if (want != null)
				{
					TowerDefensePacketOverride ov = new TowerDefensePacketOverride();
					ov.type = want._GetType();
					ov.cost = want.GetCostBeforeModifiers();
					ov.costRise = want.GetCostRise();
					ov.costMultiple = want.GetCostMultiple();
					// ★ v1.4.3（用户："冷却时间还不对"）：**冷却也要跟随被模仿植物**。
					//   `GetPacketCooldown()` / `GetStartingCooldown()` 的取值顺序（源码实证）：
					//       _override.xxx ≠ -1 ⇒ 用它 × 倍率；否则用 characterConfig.xxx × 倍率
					//   倍率里含 `ApplyMapPacketCooldownRules(_GetType(), …)` —— 我们的
					//   `_GetType()` 已被设成目标植物的 ⇒ 倍率一致
					//   ⇒ **这里必须写"基础值"**（`characterConfig` 上的原始值），
					//     写成 `want.GetPacketCooldown()` 会把倍率乘两次 ✗
					TowerDefenseCharacterConfig wc = want.characterConfig;
					if (wc != null && GodotObject.IsInstanceValid(wc))
					{
						ov.packetCooldown = wc.packetCooldown;
						ov.startingCooldown = wc.startingCooldown;
					}
					cfg._override = ov;
				}
				else
				{
					cfg._override = null;      // 还原成模仿者自己的卡面
				}
				// ② 预览片段/偏移/缩放 也要先设好 —— `CreateSprite()` 会用它选动画
				if (refCfg != null && GodotObject.IsInstanceValid(refCfg))
				{
					cfg.packetAnimeClip = refCfg.packetAnimeClip;
					cfg.packetAnimeOffset = refCfg.packetAnimeOffset;
					cfg.packetAnimeScale = refCfg.packetAnimeScale;
				}
			}

			// ── 换字典 → 重建卡面 → 立刻还原字典（这一段是原子的）──
			bool hadKey = dict.ContainsKey(MyKey);
			Resource orig = hadKey ? dict[MyKey] : null;
			dict[MyKey] = targetScene;
			try
			{
				if (cfg != null)
				{
					try { card.Init(cfg); }        // 重算费用/背景 + 内部 CreateSprite
					catch { }
				}
				try { card.CreateSprite(); }        // 保险再重建一次（幂等）
				catch { }
			}
			finally
			{
				if (hadKey)
				{
					dict[MyKey] = orig;
				}
				else
				{
					dict.Remove(MyKey);
				}
			}

			// ★ v1.4.4：**皮肤（装扮）也要跟随被模仿植物**
			ApplyTargetSkin(card, want);
		}
		catch (Exception ex)
		{
			if (_diag < 42)
			{
				_diag = 42;
				Log("换卡面异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// ★ v1.4.4：把**被模仿植物的皮肤（装扮）**套到卡面精灵上。
	///
	/// 源码依据：
	///   · 皮肤键存在存档里 —— `XWModPlayerProgressService.GetPacketState(saveKey)["Key"]["Custom"]`
	///     （`InformationPanel.EquipmentButtonPressed()` 就是往这儿写的）；
	///   · 应用方式是**图层替换**（不是换精灵场景）——
	///     `characterConfig.customData.SetCustomFliters(sprite, customKey)`
	///     然后 `sprite.UpdateMediaReplaceData(); sprite.UpdateChild();`
	///     （见 `TowerDefenseInGamePacketShow.OnCharacterSkinSwitched`）。
	/// 我们的卡面精灵是从**被模仿植物**的场景实例化出来的，但皮肤滤镜没人给它套
	/// （卡自己的 `config.saveKey` 是模仿者，收不到那个植物的皮肤切换事件）
	/// ⇒ 这里手动补上。
	/// </summary>
	private void ApplyTargetSkin(TowerDefenseInGamePacketShow card, TowerDefensePacketConfig want)
	{
		try
		{
			if (want == null || !GodotObject.IsInstanceValid(want) || string.IsNullOrEmpty(want.saveKey))
			{
				return;
			}
			object spObj = GetMember(card, "sprite");
			if (!(spObj is AdobeAnimateSprite spr) || !GodotObject.IsInstanceValid(spr))
			{
				return;
			}
			// 用**全局**配置（不是每局的副本）取 customData，保证皮肤表是全的
			TowerDefensePacketConfig global = TowerDefenseManager.GetPacketConfig(want.saveKey);
			if (global == null || !GodotObject.IsInstanceValid(global) || global.characterConfig == null)
			{
				return;
			}
			CharacterCustomData cd = global.characterConfig.customData;
			if (cd == null || !GodotObject.IsInstanceValid(cd))
			{
				return;
			}
			string skinKey = "";
			try
			{
				Godot.Collections.Dictionary st = XWModPlayerProgressService.GetPacketState(want.saveKey);
				if (st != null && st.ContainsKey("Key"))
				{
					Godot.Collections.Dictionary kd = st["Key"].AsGodotDictionary();
					if (kd != null && kd.ContainsKey("Custom"))
					{
						skinKey = kd["Custom"].AsString();
					}
				}
			}
			catch { }

			cd.ClearCustomFliters(spr);
			if (!string.IsNullOrEmpty(skinKey) && cd.customDictionary.ContainsKey(skinKey))
			{
				cd.SetCustomFliters(spr, skinKey);
			}
			spr.UpdateMediaReplaceData();
			spr.UpdateChild();
			// ★★ v1.4.5：卡面精灵是**冻结预览态**（游戏 `CreateSprite()` →
			//   `FreezePreparedPreviewTree()` 会 `SetFrozenPreview(true)`），
			//   改完滤镜/媒体替换后**必须重新提交一次渲染**，否则画面还停在旧帧
			//   ⇒ 表现就是"皮肤没生效"。
			//   对照游戏自己的 `TowerDefenseInGamePacketShow.OnCharacterSkinSwitched()`：
			//       ClearCustomFliters → SetCustomFliters → UpdateMediaReplaceData
			//       → UpdateChild → RefreshManagedSlotSpriteCacheForRender() → FreezePreviewTree(forcePoseRefresh:true)
			//   其中 `RefreshManagedSlotSpriteCacheForRender()` 是 **internal**
			//   （跨程序集调不到）⇒ 用公开的 `QueueRedraw()` +
			//   `EnsureFrozenPreviewRenderSubmission()` 做等价替代：
			//   `FreezePreviewTree()` 里那句 `EnsureFrozenPreviewRenderSubmission()`
			//   就是"冻结态下重新提交渲染"的公开入口。
			spr.QueueRedraw();
			if (spr.IsFrozenPreview)
			{
				spr.EnsureFrozenPreviewRenderSubmission();
			}
			Log("卡面已套用被模仿植物的皮肤：" + want.saveKey + " / 「" + skinKey + "」");
		}
		catch (Exception ex)
		{
			if (_diag < 45)
			{
				_diag = 45;
				Log("套皮肤异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// ★ v1.6.1（用户要求）：**卡面显示的是被模仿植物时，把卡面里的植物变灰**
	/// —— 一眼就能区分"这是模仿卡"和"这是真的那张卡"；还原成模仿者本体时取消灰度。
	///
	/// ── 为什么用 `SetRenderGrayscale` 而不是自己叠 `Modulate` ──────────
	/// 源码实证：游戏自己的"模仿者"标识走的就是这一套 ——
	///   · `TowerDefensePlantImitater.Explode()` / `TowerDefensePlantImitaterW` /
	///     `TowerDefenseZombieNormalImitater` 给变出来的角色加的都是
	///     `SetSpriteGroupShaderParameter("imitater", true)`；
	///   · `ShaderEffectComponent` 处理 `imitater` 这个着色器标记时执行的就是
	///     `adobeAnimateSpriteBase.SetRenderGrayscale(IsImitaterEnabled())`
	///     （`ShaderEffectComponent.cs:245`）。
	/// ⇒ 卡面精灵同样是 `AdobeAnimateSprite`，直接调它的 `SetRenderGrayscale(bool)`
	///   就是**与游戏本体同一套灰度渲染**（真去色，而不是把图压暗）。
	///   `SetRenderGrayscale` 内部会 `InvalidateRenderSnapshotCache(force:true)` +
    ///   `RequestNodeRedraw()`；卡面是**冻结预览态**时还要再提交一次渲染
	///   （与本文件 `ApplyTargetSkin` 的收尾动作一致）。
	/// </summary>
	private void ApplyMimicGrayscale(TowerDefenseInGamePacketShow card, bool grey)
	{
		try
		{
			object spObj = GetMember(card, "sprite");
			if (!(spObj is AdobeAnimateSprite spr) || !GodotObject.IsInstanceValid(spr))
			{
				return;
			}
			if (spr.IsRenderGrayscaleEnabled() == grey)
			{
				return;                       // 已一致 ⇒ 别重复让渲染快照失效
			}
			spr.SetRenderGrayscale(grey);
			spr.QueueRedraw();
			if (spr.IsFrozenPreview)
			{
				spr.EnsureFrozenPreviewRenderSubmission();
			}
			if (_greyDiag < 10)
			{
				_greyDiag++;
				Log("模仿者卡面灰度：" + (grey ? "开（显示的是被模仿植物）" : "关（模仿者本体）"));
			}
		}
		catch (Exception ex)
		{
			if (_diag < 48)
			{
				_diag = 48;
				Log("卡面灰度异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>卡面灰度诊断计数。</summary>
	private int _greyDiag;

	/// <summary>
	/// ★ v1.7.0（用户要求）：按配置决定"**模仿者变身出来的那株植物**"要不要变灰。
	///
	/// ── 现状（源码实证）──────────────────────────────────────────────
	/// 变出来的植物**本来就是灰的**：`TowerDefensePlantImitater.Explode()`
	/// （我们 Mod 的植物 `ImitaterClassic` 继承它 ⇒ 走同一段代码）在
	/// `packetConfig.Plant(gridPos)` 之后就是
	/// `character.CallDeferred("SetSpriteGroupShaderParameter", "imitater", true)`
	/// —— 游戏内置模仿者就是这个表现。
	/// ⇒ **开关"开"时本函数什么都不做**（保持游戏原生，零开销、零风险）。
	///
	/// ── 开关"关"时怎么取消 ─────────────────────────────────────────
	/// 用游戏自己的通道反向摘掉：对每株"当前处于灰度渲染"的植物调
	/// `SetSpriteGroupShaderParameter("imitater", false)`（`ShaderEffectComponent`
	/// 会据此把 `SetRenderGrayscale(false)` 重新应用到它的精灵上）。
	/// 判据用 `TowerDefenseCharacter.sprite.IsRenderGrayscaleEnabled()` ——
	/// 角色精灵上的灰度**只**由 `ShaderEffectComponent` 的 `imitater` 标记驱动
	/// （`ShaderEffectComponent.cs:245`），所以这个判据恰好等价于"被加了 imitater 标记"，
	/// 且是公开 API，不用反射读私有 `_effectFlags`。
	/// ⚠️ 因此"关"会取消**所有**由模仿者变身产生的灰化（含游戏内置模仿者变出来的），
	///    不只本 Mod 的 —— 这是刻意的：两者本来就是同一套机制。
	/// </summary>
	private void EnforceMimicPlantGrayscale()
	{
		if (_greyMimicPlant)
		{
			return;                     // 默认：保持游戏原生（变出来的植物是灰的）
		}
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			foreach (Variant item in mgr.GetCharacter())
			{
				try
				{
					TowerDefenseCharacter c = item.AsGodotObject() as TowerDefenseCharacter;
					if (c == null || !GodotObject.IsInstanceValid(c) || !(c is TowerDefensePlant))
					{
						continue;
					}
					AdobeAnimateSprite spr = c.sprite;
					if (spr == null || !GodotObject.IsInstanceValid(spr) || !spr.IsRenderGrayscaleEnabled())
					{
						continue;
					}
					c.SetSpriteGroupShaderParameter("imitater", false);
					if (_greyDiag < 10)
					{
						_greyDiag++;
						Log("已按配置取消「复制出的植物」的灰度：" + (c.config != null ? c.config.name : c.GetType().Name));
					}
				}
				catch { }
			}
		}
		catch { }
	}

	/// <summary>读 P2-9 的两项设置：是否加阳光消耗、加几倍。</summary>
	/// <summary>读「是否允许多次复制」设置（bool，默认 true = 选卡可多张）。</summary>
	private void ReadMultiCopySetting(XWModRuntimeContext context)
	{
		try
		{
			if (context == null) { return; }
			object v = context.Settings.Get<object>(KMultiCopy);
			bool before = _allowMultipleCopy;
			if (v is bool b) { _allowMultipleCopy = b; }
			else if (v is string s && bool.TryParse(s, out bool sb)) { _allowMultipleCopy = sb; }
			else if (v is System.Text.Json.JsonElement je
				&& (je.ValueKind == System.Text.Json.JsonValueKind.True || je.ValueKind == System.Text.Json.JsonValueKind.False))
			{
				_allowMultipleCopy = je.GetBoolean();
			}
			try { if (EnableLog) { GD.Print(P + "多次复制设置：raw=" + (v?.ToString() ?? "null") + " ⇒ " + _allowMultipleCopy); } } catch { }
		}
		catch (Exception ex) { try { Log("多次复制设置读取异常：" + ex.Message); } catch { } }
	}

	private void ReadSunCostSettings(XWModRuntimeContext context)
	{
		try
		{
			if (context == null) { return; }
			object b = context.Settings.Get<object>(KAddSunCost);
			if (b is bool bb) { _addSunCost = bb; }
			else if (b is System.Text.Json.JsonElement jb
				&& (jb.ValueKind == System.Text.Json.JsonValueKind.True || jb.ValueKind == System.Text.Json.JsonValueKind.False))
			{
				_addSunCost = jb.GetBoolean();
			}
			object m = context.Settings.Get<object>(KSunMul);
			if (m is System.Text.Json.JsonElement jm && jm.ValueKind == System.Text.Json.JsonValueKind.String
				&& int.TryParse(jm.GetString(), out int n1)) { _sunMul = n1; }
			else if (m is string sm && int.TryParse(sm, out int n2)) { _sunMul = n2; }
			else { try { _sunMul = System.Convert.ToInt32(m); } catch { } }
			if (_sunMul < 1) { _sunMul = 1; }
			if (_sunMul > 10) { _sunMul = 10; }
		}
		catch { }
	}

	/// <summary>★ v1.7.0：读两个「变灰」开关（缺省 = 保持默认值）。</summary>
	private void ReadGreySettings(XWModRuntimeContext context)
	{
		try
		{
			if (context == null) { return; }
			object c = context.Settings.Get<object>(KGreyCard);
			if (c is bool bc) { _greyMimicCard = bc; }
			else if (c is string sc && bool.TryParse(sc, out bool sb)) { _greyMimicCard = sb; }
			else if (c is System.Text.Json.JsonElement jc
				&& (jc.ValueKind == System.Text.Json.JsonValueKind.True || jc.ValueKind == System.Text.Json.JsonValueKind.False))
			{
				_greyMimicCard = jc.GetBoolean();
			}
			object p = context.Settings.Get<object>(KGreyPlant);
			if (p is bool bp) { _greyMimicPlant = bp; }
			else if (p is string sp && bool.TryParse(sp, out bool sb2)) { _greyMimicPlant = sb2; }
			else if (p is System.Text.Json.JsonElement jp
				&& (jp.ValueKind == System.Text.Json.JsonValueKind.True || jp.ValueKind == System.Text.Json.JsonValueKind.False))
			{
				_greyMimicPlant = jp.GetBoolean();
			}
		}
		catch { }
	}

	/// <summary>
	/// ★ v1.4.4 起：**让卡面价格跟着被模仿植物同步变动**；v1.7.1 起倍数改为乘在"变动后的价格"上。
	///
	/// 源码依据 `TowerDefenseInGamePacketShow.RefreshDynamicItemCost()`（游戏对**每张卡自己**的算价式子）：
	/// ```csharp
	/// baseItemCost = config.GetCost();
	/// long num = baseItemCost;
	/// if (!TowerDefenseManager.MapIgnoresDynamicPacketCostGrowth(config._GetType())) {
	///     int characterNum = TowerDefenseManager.Instance.GetCharacterNum(config.saveKey);  // ★ 按 saveKey 数
	///     if (costMultiple != -1.0) num = floor(num * pow(costMultiple, characterNum));
	///     if (riseCost != -1)      num += characterNum * riseCost;
	/// }
	/// itemCost = num;
	/// ```
	/// ⇒ 游戏是**按卡自己的 `saveKey`** 数"场上有几株"来涨价的；我们的卡 saveKey 是
	/// `ImitaterClassic`（种下后立刻变身走人，计数恒 0）⇒ **金/钻/彩等"越种越贵"的植物
	/// 涨价完全跟不上**。
	/// ⇒ 这里按**被模仿植物的 saveKey** 照抄这条式子重算，直接写 `card.itemCost`
	/// （该属性 setter 内部会自动刷标签；`PacketShow.TryResolveModItemCost()` 里
	///  `cost = itemCost` ⇒ **显示与扣费是同一个值**）。
	/// 基准价用 `want.GetCost()` —— 它把目标植物自己的**变价行为**（关卡改价/夜间价等）也算进去。
	/// </summary>
	private void SyncMimicCost(TowerDefenseInGamePacketShow card, TowerDefensePacketConfig want)
	{
		try
		{
			if (want == null || !GodotObject.IsInstanceValid(want))
			{
				return;
			}
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			long v = (long)want.GetCost();
			// ── ★ v1.7.1：**先算出目标植物"当前的真实价格"（含它自己的涨价/变价），最后再乘用户倍数** ──
			//   源码依据 `TowerDefenseInGamePacketShow.RefreshDynamicItemCost()`：
			//       baseItemCost = config.GetCost();
			//       n = GetCharacterNum(config.saveKey);
			//       if (costMultiple != -1) num = floor(num * pow(costMultiple, n));
			//       if (riseCost != -1)      num += n * riseCost;        // ← 涨价在乘法**之后**
			//   ⇒ 这里完全照抄这条式子，但用**目标植物自己的** `GetCostMultiple()/GetCostRise()`
			//     （不能用本卡那两个字段：本卡 `_override` 在这两项为 -1 时会回落到
			//      "模仿者自己"的 `characterConfig`，口径就错了）。
			//   ⚠️ 旧写法是"先把用户倍数乘在**基准价**上，再走涨价" ⇒ 涨价那一段永远拿不到倍数：
			//      目标 400 + 涨价 100×n、×2 时显示 400×2 + 100×n = 900（用户实测），
			//      而正确值应是 (400 + 100×n) × 2 = 1200。
			if (!TowerDefenseManager.MapIgnoresDynamicPacketCostGrowth(want._GetType()))
			{
				int n = mgr.GetCharacterNum(want.saveKey);
				double mul = want.GetCostMultiple();
				if (mul != -1.0 && n > 0)
				{
					double d = (double)v * Math.Pow(mul, n);
					if (!double.IsNaN(d) && d > 0.0 && d <= 9.223372036854776E18)
					{
						v = (long)Math.Floor(d);
					}
				}
				int rise = want.GetCostRise();
				if (rise != -1)
				{
					v += (long)n * (long)rise;
				}
			}
			// ★ P2-9：最后才乘"复制时增加阳光消耗"的倍数 ⇒ 倍数作用于**变动后的价格**
			if (_addSunCost && _sunMul > 1)
			{
				long boosted = v * _sunMul;
				if (boosted > 0) { v = boosted; }
			}
			if (card.itemCost != v)
			{
				card.itemCost = v;
			}
		}
		catch { }
	}

	/// <summary>
	/// ★ v1.5.0：取"这张卡槽里的模仿者卡应该跟随哪一张" —— 查 <see cref="_bankTargets"/>
	/// （由 <see cref="DriveSeedBank"/> 每帧按"左邻最近实卡"重建）；
	/// 查不到（这张卡不在卡槽 / 表还没建）时回退到全局 <see cref="_lastPlant"/>。
	/// </summary>
	private TowerDefensePacketConfig TargetOfCard(TowerDefenseInGamePacketShow card)
	{
		try
		{
			if (card != null && GodotObject.IsInstanceValid(card))
			{
				if (_bankTargets.TryGetValue(card.GetInstanceId(), out TowerDefensePacketConfig t)
					&& t != null && GodotObject.IsInstanceValid(t))
				{
					return t;
				}
				// ── 兜底：表里没有（例如卡刚被拿起、本帧表还没建到它）⇒
				//    直接在 `packetList` 里按**位置**往前找最近一张实卡。
				TowerDefenseManager mgr = TowerDefenseManager.Instance;
				TowerDefenseInGameSeedBank sb = (mgr != null && GodotObject.IsInstanceValid(mgr))
					? mgr.GetSeedBank() : null;
				if (sb != null && GodotObject.IsInstanceValid(sb))
				{
					TowerDefensePacketConfig prev = null;
					foreach (TowerDefenseInGamePacketShow c in sb.packetList)
					{
						if (c == null || !GodotObject.IsInstanceValid(c))
						{
							continue;
						}
						if (ReferenceEquals(c, card))
						{
							if (prev != null)
							{
								return prev;
							}
							break;         // 它是第一张 ⇒ 没有左邻 ⇒ 走下面全局兜底
						}
						if (c.originalSaveKey == MyKey)
						{
							continue;
						}
						TowerDefensePacketConfig cf = SafeConfig(c);
						if (cf != null && cf.saveKey != MyKey && cf.characterConfig is TowerDefensePlantConfig)
						{
							prev = cf;
						}
					}
				}
			}
		}
		catch { }
		return (_lastPlant != null && GodotObject.IsInstanceValid(_lastPlant)) ? _lastPlant : null;
	}

	/// <summary>
	/// ★★ v1.5.0：把"变身目标卡池"指向**当前拿起的那张模仿者卡的目标**。
	///
	/// 为什么必须在"拿起时"决定：内置 `TowerDefensePlantImitater.Explode()`
	/// （种下、旋转动画播完之后才跑）是从角色的 `packetBank` 里
	/// `GetCategory("White") + GetCategory("Original")` 再 `PickRandom()` 抽一张
	/// ⇒ 我们只要把池收窄成"只剩目标那一张"结果就确定（见 <see cref="PointColourBankAt"/>）。
	/// 但卡槽里可能**同时存在多张目标不同**的模仿者卡，而池只能有一份内容
	/// ⇒ 唯一正确的时机就是**玩家拿起卡的那一刻**（要种下必然先拿起）。
	/// 放下后**继续锁存、不还原** —— 因为 `Explode()` 那时才跑（见 <see cref="_latchedTarget"/>）。
	/// 拿起非模仿者卡时**不动**（模仿者以外的植物不会走 `Explode()`）。
	/// </summary>
	private void PointColourBankAtHeld()
	{
		try
		{
			TowerDefenseInGamePacketShow held = GetBattleSlotPacket();
			if (held != null && GodotObject.IsInstanceValid(held) && held.originalSaveKey == MyKey)
			{
				TowerDefensePacketConfig t = TargetOfCard(held);
				if (t != null && GodotObject.IsInstanceValid(t))
				{
					if (!ReferenceEquals(_latchedTarget, t))
					{
						Log("拿起模仿者卡 ⇒ 变身目标锁存为 " + t.saveKey);
					}
					_latchedTarget = t;
				}
			}
			TowerDefensePacketConfig use = (_latchedTarget != null && GodotObject.IsInstanceValid(_latchedTarget))
				? _latchedTarget
				: _lastPlant;
			PointColourBankAt(use);
		}
		catch { }
	}

	/// <summary>
	/// 把自定义卡池 `ImitaterClassicColour` 的 `White` 收窄成"只有 `want` 这一张"，
	/// 这样模仿者 `Explode()` 里的 `array.PickRandom()` 必然抽到它 ⇒ 变身结果确定。
	/// `want == null` 时**不动**（保持满池，随机取卡玩法那条路继续可用）。
	/// </summary>
	private void PointColourBankAt(TowerDefensePacketConfig want)
	{
		try
		{
			if (want == null || !GodotObject.IsInstanceValid(want) || string.IsNullOrEmpty(want.saveKey))
			{
				return;
			}
			TowerDefensePacketBankData bank = TowerDefenseManager.GetPacketBankData(CustomBank);
			if (bank == null || !GodotObject.IsInstanceValid(bank))
			{
				return;
			}
			Godot.Collections.Array arr = bank.GetCategory("White");
			if (arr != null && arr.Count == 1 && arr[0].AsString() == want.saveKey)
			{
				return;                     // 已经指向它了 ⇒ 幂等
			}
			var one = new Godot.Collections.Array();
			one.Add(want.saveKey);
			bank.category["White"] = one;
			bank.category["Original"] = new Godot.Collections.Array();
			Log("变身目标已锁定：" + want.saveKey);
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
	// ── ★ 性能：卡片节点列表缓存（整树递归很贵，15 帧内复用同一份）──
	private readonly System.Collections.Generic.List<Node> _packetCache = new System.Collections.Generic.List<Node>();
	private ulong _packetCacheFrame;

	/// <summary>
	/// 取场上全部 `TowerDefenseInGamePacketShow`（卡池 + 卡槽 + 图鉴里的卡）。
	/// 旧实现每帧整树递归 + 新建 List：安卓实测这类"每帧全树走"约 3~5ms/帧
	/// （同一模式在共享的 SponsorQrInjector 上实测 1155~1715µs/1226 节点，安卓更慢）。
	/// 卡片集合不可能每帧变化 ⇒ 15 帧复用一次，并逐个校验节点仍有效。
	/// </summary>
	private System.Collections.Generic.List<Node> PacketListCached()
	{
		try
		{
			ulong f = Engine.GetProcessFrames();
			if (_packetCache.Count > 0 && f - _packetCacheFrame < 15UL)
			{
				bool ok = true;
				for (int i = 0; i < _packetCache.Count; i++)
				{
					Node n = _packetCache[i];
					if (n == null || !GodotObject.IsInstanceValid(n)) { ok = false; break; }
				}
				if (ok) { return _packetCache; }
			}
			_packetCacheFrame = f;
			_packetCache.Clear();
			CollectByClassName(_tree != null && GodotObject.IsInstanceValid(_tree) ? _tree.Root : null,
				0, "TowerDefenseInGamePacketShow", _packetCache, 400);
			return _packetCache;
		}
		catch { return _packetCache; }
	}

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
			// ★ 性能（2026-10-12）：不再每帧整树重扫（安卓上 3~5ms/帧）—— 见 PacketListCached()
			var cards = PacketListCached();
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
				// ★ v1.6.0：「允许多次复制」⇒ 把这张卡池卡的**按压语义**从游戏的
				//   `PacketChoose`（再点一次会被当成"取消"）换成"再多选一张"（见
				//   OnMimicPoolPressed / MaintainMimicPoolPress）。开关关掉时完整还原原生行为。
				//   每帧维护（幂等 `-=`/`+=`）：卡池卡是虚拟化复用的，`ResetForPool()` 会把
				//   `OnPressed` 整个清空，只靠"打过标记"会漏挂。
				MaintainMimicPoolPress(p);
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

	// ================================================================ ③-b 「允许多次复制」：卡池点一次 = 卡槽多一张
	//
	// 用户口径（2026-10-12 当面确认）：要的是**选卡阶段能一次选多张模仿者**。
	//
	// ── 为什么原样点不出来（源码实证）──────────────────────────────
	// `TowerDefenseBattleFeaturePacketBank.PacketChoose(packet)` 的第一件事就是
	// `FindSelectedPacket(packet.config.saveKey)`：卡槽里**已经有一张**同身份的卡时，
	// 这一次点击被当成**取消**（`DeletePacket` + `PacketAlive`）⇒ 永远只能有一张。
	// 日志实锤：卡槽 `[…][SunShroom][模仿者]` 再点一次 ⇒ `[…][SunShroom]`（模仿者消失），
	// 点第三次才又在末尾出现一张。
	//
	// ── 做法（v1.6.0 定稿：**本 Mod 独占这张卡的按压语义**）─────────
	// 开「允许多次复制」时，把游戏的 `PacketChoose` 从这个**卡池卡**的 `OnPressed` 上摘掉，
	// 换上本 Mod 的 `OnMimicPoolPressed`（每帧幂等维护，见 MaintainMimicPoolPress）：
	//   · 每次点击**严格 +1 张**——走游戏自己的 `packetBank.CreateAnime()`（深拷贝 config →
	//     `seedBank.AddPacket` → 飞入动画），与原生 `PacketChoose` 的追加路径**逐字同一条**；
	//   · **不删任何已有卡** ⇒ 多张模仿者各自的"左邻目标"不会被破坏；
	//   · 卡池那张保持 `alive / select` ⇒ 可以继续点。
	// 想**减少**一张照旧：点卡槽里那张模仿者卡（原生 `DeletePacket`）。
	// ⚠️ 摘掉的只是"点这张卡"这一条路径 —— `PacketListChoose` / `PacketChooseFromName` /
	//    「重新选卡」都是**直接调用** `PacketChoose(...)`，完全不受影响。
	// ⚠️ 关掉开关时 MaintainMimicPoolPress 会把 `PacketChoose` 原样挂回去（完整还原原生行为）。

	/// <summary>多次复制诊断计数。</summary>
	private int _multiCopyDiag;

	/// <summary>
	/// 卡池那张模仿者卡被按下时的处理器（**本 Mod 独占它的按压语义**，见 ③-b 段头）。
	///
	/// 只做一件事：**往卡槽里再加一张**（严格 +1），然后让卡池那张保持可点。
	/// 不删卡、不需要"点击前后张数差"补偿 —— 因为游戏的取消分支已经被摘掉了。
	/// </summary>
	private void OnMimicPoolPressed(TowerDefenseInGamePacketShow card)
	{
		try
		{
			if (!_allowMultipleCopy)
			{
				return;                  // 关着 ⇒ 语义已还原给游戏的 PacketChoose
			}
			if (card == null || !GodotObject.IsInstanceValid(card))
			{
				return;
			}
			// 卡池节点会被复用（`ResetForPool()` + `BindVirtualizedPacket()` 绑到别的植物）
			// ⇒ 必须复核"此刻它还是模仿者卡、且还挂在选卡界面卡池下"
			TowerDefensePacketConfig cfg = SafeConfig(card);
			if (cfg == null || cfg.saveKey != MyKey || !IsInChooseBank(card))
			{
				return;
			}
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			TowerDefenseInGameSeedBank seedBank = (mgr != null && GodotObject.IsInstanceValid(mgr))
				? mgr.GetSeedBank() : null;
			if (seedBank == null || !GodotObject.IsInstanceValid(seedBank) || seedBank.hasGameStarted == true)
			{
				return;                  // 只在**选卡阶段**接管；进战斗后交给 ② 的 CD 重置逻辑
			}
			if (!seedBank.CanAddPacket())
			{
				// 卡槽已满：与原生 `PacketChoose` 的满槽分支一致（`packet.Reset(); return;`）
				// —— 不硬塞、也不把卡面留成"已选中"的样子
				try { card.Reset(); } catch { }
				if (_multiCopyDiag < 30)
				{
					_multiCopyDiag++;
					Log("多次复制：卡槽已满，本次未追加。");
				}
				return;
			}
			// ★ v1.7.0：同一张植物只能被一张模仿卡模仿 —— 新卡会被追加到**卡槽末尾**，
			//   它的"左邻实卡"就是牌组里最后一张植物 ⇒ 若这张植物已被别的模仿卡占用，
			//   本次点击不追加（先选另一张植物再点），避免"连点两次得到两张一模一样的模仿卡"。
			TowerDefensePacketConfig candidate = LastRealPlant(seedBank);
			if (candidate == null)
			{
				try { card.Reset(); } catch { }
				if (_dupDiag < 20)
				{
					_dupDiag++;
					Log("还没有选择任何植物 ⇒ 模仿者无处可模仿，本次未追加（先选一张植物再点）。");
				}
				return;
			}
			if (IsImitationClaimed(seedBank, candidate))
			{
				try { card.Reset(); } catch { }
				if (_dupDiag < 20)
				{
					_dupDiag++;
					Log("「" + candidate.saveKey + "」已经被另一张模仿者卡模仿 ⇒ 同一张植物只能被一张模仿卡模仿，"
						+ "先选另一张植物再点。");
				}
				return;
			}
			int added = AddMimicPacket(card) ? 1 : 0;
			KeepPoolCardClickable(card);
			if (_multiCopyDiag < 30)
			{
				_multiCopyDiag++;
				Log("多次复制：点卡池模仿者 ⇒ 卡槽追加 " + added + " 张（现有 "
					+ CountMimics(seedBank) + " 张）");
			}
		}
		catch (Exception ex)
		{
			if (_diag < 46)
			{
				_diag = 46;
				Log("多次复制追加异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>卡槽里**最后一张实卡**（= 新模仿者卡追加到末尾后会模仿的那张）。</summary>
	private static TowerDefensePacketConfig LastRealPlant(TowerDefenseInGameSeedBank seedBank)
	{
		TowerDefensePacketConfig last = null;
		try
		{
			if (seedBank == null || !GodotObject.IsInstanceValid(seedBank) || seedBank.packetList == null)
			{
				return null;
			}
			foreach (TowerDefenseInGamePacketShow c in seedBank.packetList)
			{
				if (c == null || !GodotObject.IsInstanceValid(c) || c.originalSaveKey == MyKey)
				{
					continue;
				}
				TowerDefensePacketConfig cf = SafeConfig(c);
				if (cf != null && cf.saveKey != MyKey && cf.characterConfig is TowerDefensePlantConfig)
				{
					last = cf;            // 越靠后越新
				}
			}
		}
		catch { }
		return last;
	}

	/// <summary>卡槽里是否已经有模仿者卡在模仿这张植物（"同一张植物只能被一张模仿卡模仿"）。</summary>
	private bool IsImitationClaimed(TowerDefenseInGameSeedBank seedBank, TowerDefensePacketConfig plant)
	{
		try
		{
			if (seedBank == null || !GodotObject.IsInstanceValid(seedBank) || plant == null
				|| string.IsNullOrEmpty(plant.saveKey))
			{
				return false;
			}
			foreach (TowerDefenseInGamePacketShow c in seedBank.packetList)
			{
				if (c == null || !GodotObject.IsInstanceValid(c) || c.originalSaveKey != MyKey)
				{
					continue;
				}
				TowerDefensePacketConfig t = TargetOfCard(c);
				if (t != null && GodotObject.IsInstanceValid(t) && t.saveKey == plant.saveKey)
				{
					return true;
				}
			}
		}
		catch { }
		return false;
	}

	/// <summary>卡池那张保持"还能点"（`alive` 决定 `Pressed()` 的守卫；`select` 决定选中框）。</summary>
	private static void KeepPoolCardClickable(TowerDefenseInGamePacketShow card)
	{
		try
		{
			if (!card.alive)
			{
				card.alive = true;
			}
			card.select = true;
		}
		catch { }
	}
	/// <summary>
	/// 每帧维护卡池那张模仿者卡的按压语义（幂等）：
	///   · 开关开 ⇒ 摘掉游戏的 `PacketChoose`，只留本 Mod 的 `OnMimicPoolPressed`；
	///   · 开关关 ⇒ 摘掉本 Mod 的，把 `PacketChoose` 原样挂回（还原原生"再点=取消"）。
	///
	/// 为什么用"每帧 `-=` / `+=`"而不是"打过标记就不再重挂"：卡池卡是**虚拟化复用**的，
	/// `RefreshVirtualizedPacketBindings()` 里 `UnbindVirtualizedPacket()` + `ResetForPool()`
	/// 会把 `OnPressed` **整个清空**（`ClearEventHandlers()` 里 `OnPressed = null`）——
	/// 标记会活着但订阅已经没了。而 `Delegate.Remove` 只移除**最后一次**匹配项、没订阅时是
	/// no-op ⇒ `-=` 后 `+=` 恒等于"恰好一份、且在队尾（跑在游戏处理器之后）"，零反射、零状态。
	/// </summary>
	private void MaintainMimicPoolPress(TowerDefenseInGamePacketShow card)
	{
		try
		{
			TowerDefenseBattleFeaturePacketBank bank = GetChooseBankFeature();
			if (bank == null || !GodotObject.IsInstanceValid(bank))
			{
				return;
			}
			if (_allowMultipleCopy)
			{
				card.OnPressed -= bank.PacketChoose;      // 换成"再加一张"
				card.OnPressed -= OnMimicPoolPressed;
				card.OnPressed += OnMimicPoolPressed;
			}
			else
			{
				card.OnPressed -= OnMimicPoolPressed;     // 完整还原原生行为
				card.OnPressed -= bank.PacketChoose;
				card.OnPressed += bank.PacketChoose;
			}
		}
		catch { }
	}

	/// <summary>选卡界面那个卡池 feature（`GetPacketBankFeature()` 是**实例**方法，必须先拿 Instance）。</summary>
	private static TowerDefenseBattleFeaturePacketBank GetChooseBankFeature()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return null;
			}
			TowerDefenseBattleFeaturePacketBank bank = mgr.GetPacketBankFeature();
			return (bank != null && GodotObject.IsInstanceValid(bank)) ? bank : null;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>卡槽里"身份 = 本 Mod 模仿者"的卡有几张（身份口径与游戏一致：`originalSaveKey`）。</summary>
	private static int CountMimics(TowerDefenseInGameSeedBank seedBank)
	{
		int n = 0;
		try
		{
			if (seedBank == null || !GodotObject.IsInstanceValid(seedBank) || seedBank.packetList == null)
			{
				return 0;
			}
			foreach (TowerDefenseInGamePacketShow c in seedBank.packetList)
			{
				if (c != null && GodotObject.IsInstanceValid(c) && c.originalSaveKey == MyKey)
				{
					n++;
				}
			}
		}
		catch { }
		return n;
	}

	/// <summary>
	/// 往卡槽追加一张模仿者卡 —— 走游戏自己的 `TowerDefenseInGamePacketBank.CreateAnime()`，
	/// 也就是原生 `PacketChoose` 追加时用的那条路（源码实证）：
	/// `CreateAnime(config, pos)` 内部 = 深拷贝 config → `seedBank.AddPacket(dup, IsGameRunning())`
	/// → 飞入动画 → 动画结束后把卡显示出来。
	/// 新卡由 `AddPacket` 自己挂上 `OnPressed += DeletePacket`（`isStart == false`）
	/// ⇒ 卡槽里点它 = 移除，和原生一致。
	/// </summary>
	private bool AddMimicPacket(TowerDefenseInGamePacketShow poolCard)
	{
		try
		{
			TowerDefenseBattleFeaturePacketBank bank = GetChooseBankFeature();
			if (bank == null || !GodotObject.IsInstanceValid(bank))
			{
				return false;
			}
			TowerDefenseInGamePacketBank gui = bank.packetBank;
			if (gui == null || !GodotObject.IsInstanceValid(gui))
			{
				return false;
			}
			gui.CreateAnime(poolCard.config, gui.GetCameraPos() + poolCard.GlobalPosition);
			return true;
		}
		catch (Exception ex)
		{
			if (_diag < 47)
			{
				_diag = 47;
				Log("追加模仿者卡异常（本条只报一次）：" + ex.Message);
			}
			return false;
		}
	}


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
			_bankTargets.Clear();
			_latchedTarget = null;
			_lastBankSeq = null;
			if (had)
			{
				Log("关卡切换 ⇒ 已清空「上一次选择」记录（防止跨局残留）。");
			}
		}
		catch { }
	}

	// ================================================================ 自制关卡（关卡编辑器）卡池

	/// <summary>已注入过 Mod 植物的编辑器卡池对象 ID。</summary>
	private ulong _editorDataId;

	/// <summary>
	/// ★★ v1.3.4（用户："模仿者在自制关卡页面中无法找到，也没有 mod 类植物显示"）：
	/// **自制关卡的卡池是另一套** —— `LevelEditorPacketBank`（`Prefab/GUI/LevelEditor/
	/// MapEditor/PacketBank/`），它的数据来自全局 `ResourceManager.TOWERDEFENSE_PACKETBANKS["Total"]`
	/// （`TryLoadDefaultPacketBank()`），**完全不走** `XWModContentCatalog.WithPlants()`
	/// ⇒ 选卡界面/图鉴里能看到的 Mod 植物，在关卡编辑器里一个都不显示。
	///
	/// 处理：
	///   ① 往编辑器卡池数据里注入 `ModPlants` 类别（直接借用官方 `WithPlants()`——
	///      它就是"深拷贝 category + 塞 ModPlants"，我们只取那一项，不整体替换，避免每帧深拷贝）；
	///   ② 顺带把每张 Mod 植物塞进它**自己的稀有度分类**（模仿者是彩卡 ⇒ 彩卡页也能找到）；
	///   ③ 在编辑器的分类按钮列（`VBoxContainer`）末尾补一个「Mod植物」按钮
	///      （实例化官方 `PacketCategoryButton.tscn`，外观与其它按钮一致），
	///      点击切换 `CategoryChoose("ModPlants")`。
	/// 每帧调用、幂等：按卡池对象 ID / 按钮是否存在判断。
	/// </summary>
	private void EnsureEditorBank()
	{
		try
		{
			LevelEditorPacketBank bank = LevelEditorPacketBank.Instance;
			if (bank == null || !GodotObject.IsInstanceValid(bank))
			{
				return;
			}
			TowerDefensePacketBankData data = bank.data;
			if (data == null || !GodotObject.IsInstanceValid(data))
			{
				return;
			}

			// ── ① ModPlants 类别 ──────────────────────────────────
			if (_editorDataId != data.GetInstanceId())
			{
				_editorDataId = data.GetInstanceId();
				TowerDefensePacketBankData withMods = XWModContentCatalog.WithPlants(data);
				if (withMods != null && GodotObject.IsInstanceValid(withMods)
					&& withMods.category.ContainsKey("ModPlants"))
				{
					Godot.Collections.Array mods = withMods.category["ModPlants"].AsGodotArray();
					if (mods != null && mods.Count > 0)
					{
						data.category["ModPlants"] = mods;
						Log("自制关卡卡池：已注入 Mod 植物 " + mods.Count + " 张（ModPlants）。");
					}
				}
				// ── ② 同时塞进各自稀有度分类（找不到就在里面）─────────
				foreach (XWModContentCatalog.Packet pk in XWModContentCatalog.GetPackets(plants: true))
				{
					TowerDefensePacketConfig cfg = pk.Config;
					if (cfg == null || !GodotObject.IsInstanceValid(cfg))
					{
						continue;
					}
					string cat = RarityCategoryName(cfg.type);
					if (string.IsNullOrEmpty(cat) || !data.category.ContainsKey(cat))
					{
						continue;
					}
					Godot.Collections.Array arr = data.category[cat].AsGodotArray();
					if (arr == null)
					{
						continue;
					}
					if (!arr.Contains(pk.Key))
					{
						arr.Add(pk.Key);
						data.category[cat] = arr;
						Log("自制关卡卡池：把 " + pk.Key + " 追加进「" + cat + "」分类。");
					}
				}
			}

			// ── ③ 「Mod植物」按钮（不存在才建；幂等）──────────────
			Node vb = bank.GetNodeOrNull("VBoxContainer");
			if (vb != null && vb.GetNodeOrNull("CardModPlantsEditor") == null)
			{
				AddEditorModButton(vb);
			}
		}
		catch (Exception ex)
		{
			if (_diag < 60)
			{
				_diag = 60;
				Log("自制关卡卡池处理异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>PACKET_TYPE → 卡池分类名。</summary>
	private static string RarityCategoryName(TowerDefenseEnum.PACKET_TYPE t)
	{
		switch (t)
		{
			case TowerDefenseEnum.PACKET_TYPE.WHITE: return "White";
			case TowerDefenseEnum.PACKET_TYPE.GOLD: return "Gold";
			case TowerDefenseEnum.PACKET_TYPE.DIAMOND: return "Diamond";
			case TowerDefenseEnum.PACKET_TYPE.COLOUR: return "Colour";
			case TowerDefenseEnum.PACKET_TYPE.STAR: return "Star";
			case TowerDefenseEnum.PACKET_TYPE.ORIGINAL: return "Original";
			default: return "";
		}
	}

	/// <summary>在编辑器分类按钮列末尾加「Mod植物」按钮（实例化官方按钮场景，外观一致）。</summary>
	private void AddEditorModButton(Node vbox)
	{
		try
		{
			PackedScene sc = GD.Load<PackedScene>(
				"res://Registry/Battle/Feature/PacketBank/PacketBank/PacketCategory/PacketCategoryButton.tscn");
			if (sc == null || !GodotObject.IsInstanceValid(sc))
			{
				return;
			}
			PacketCategoryButton btn = sc.Instantiate<PacketCategoryButton>(PackedScene.GenEditState.Disabled);
			if (btn == null)
			{
				return;
			}
			btn.Name = "CardModPlantsEditor";
			btn.category = "ModPlants";
			btn.Visible = true;
			Label lb = btn.GetNodeOrNull<Label>("LabelNode/Label");
			if (lb != null)
			{
				lb.Text = "Mod植物";
			}
			vbox.AddChild(btn);
			btn.OnChoose += cat => OnEditorCategoryChosen(btn, cat);
			Log("自制关卡卡池：已新增「Mod植物」分类按钮。");
		}
		catch (Exception ex)
		{
			if (_diag < 61)
			{
				_diag = 61;
				Log("新增编辑器分类按钮异常：:" + ex.Message);
			}
		}
	}

	/// <summary>编辑器分类按钮点击 ⇒ 走它自己的 `CategoryChoose`。</summary>
	private void OnEditorCategoryChosen(PacketCategoryButton btn, string cat)
	{
		try
		{
			LevelEditorPacketBank bank = LevelEditorPacketBank.Instance;
			if (bank == null || !GodotObject.IsInstanceValid(bank))
			{
				return;
			}
			bank.CategoryChoose(string.IsNullOrEmpty(cat) ? "ModPlants" : cat);
			Log("自制关卡卡池：已切到「" + cat + "」分类。");
		}
		catch (Exception ex)
		{
			if (_diag < 62)
			{
				_diag = 62;
				Log("编辑器分类切换异常：" + ex.Message);
			}
		}
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
