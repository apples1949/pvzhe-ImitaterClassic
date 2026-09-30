# -*- coding: utf-8 -*-
"""「经典模仿者」Mod —— 生成资源 → 编译 → 打包 → 装机。

设计要点（照技能 `pvz-hybrid-plant-authoring` 的 Step 3/4/7）：
  · 完全**复用内置模仿者**的艺术与脚本（决定 A + 决定 C 路线 C）：
      角色场景 instance `TowerDefensePlantImitater.tscn`
      精灵场景 instance `Imitater.tscn`
    ⇒ 不改动游戏自带的任何资源（用户明确要求"现有的模仿者植物不改动"）。
  · 唯一的差别是把场景上的 `packetBank` 指向**运行时注册的自定义卡池**
    `ImitaterClassicColour`（由插件在启动时注册，只装彩卡植物）
    ⇒ 内置模仿者 `Explode()` 里硬编码的 `GetCategory("White")` 就抽到彩卡植物。
  · `CHAR_KEY` 四处同名：目录名 / 场景文件名 / `characterConfig.name` / `packet.saveKey`。
"""
import json
import os
import shutil
import subprocess
import sys
import zipfile
import hashlib

ROOT = r"C:\Users\txgcs\WorkBuddy\zjb"
BASE = os.path.join(ROOT, "mod", "ImitaterClassic")
PY = r"C:\Users\txgcs\.workbuddy\binaries\python\versions\3.13.12\python.exe"
DOTNET = os.path.join(ROOT, "tools", "dotnet9", "dotnet.exe")
SRC = os.path.join(BASE, "runtime_src")
DIST = os.path.join(ROOT, "mod", "dist", "ImitaterClassic.pmod")

UD = r"C:\Users\txgcs\AppData\Roaming\Godot\app_userdata\植物大战僵尸杂交版"
MODS = os.path.join(UD, "Mods")
CACHE = os.path.join(UD, "ModsCache")

# ---------------- 常量（改这里就够了） ----------------
CHAR_KEY = "ImitaterClassic"
MOD_NAME = "经典模仿者"
MOD_ID = "imitaterclassic"
CFG_FILE = "TowerDefensePlantImitaterClassic.tres"
SCENE_FILE = "ImitaterClassic.tscn"
SPRITE_FILE = "ImitaterClassic.tscn"
CSET_FILE = "ImitaterClassicComponentSet.tres"
PKG = "Resources/Characters/Plants/" + CHAR_KEY
CARD_REL = "Resources/Cards/" + CHAR_KEY + ".tres"
CUSTOM_BANK = "ImitaterClassicColour"

PN = "经典模仿者"
PD = ("选中后自动复制你上一次选择的那张植物卡：卡面、阳光与冷却都按那张卡结算；"
       "种下后长出那张植物并播放模仿者变身特效，卡片随即还原，可继续复制下一张。")
# ⚠️ 文案里【禁止出现英文双引号 "】—— 本文件用 f-string 直接拼进 .tres 的字符串字面量，
#   Godot 解析到内层 " 就认为字符串结束，后文变成语法垃圾 ⇒ 字段被截断（v1.3.2 实测：
#   图鉴说明只显示到「（此时没有」）。要用引用一律用中文引号『』。
#   另：RichTextLabel 支持 \n 换行，Python 里写 \\n 才能在 .tres 里落成 \n 转义。
PHD = ("PvZ 1 代原版行为：模仿者本身没有固定形态。\\n"
       "· 选中它时，自动变成你【上一次选择】的那张植物卡；\\n"
       "· 阳光与冷却都按被复制的那张卡结算；\\n"
       "· 种下后直接长出那张植物，卡片随后还原成模仿者本体，可继续复制下一张；\\n"
       "· 还没有任何已选植物时，这张卡不可选（置灰）；\\n"
       "· 若这张卡由随机取卡类玩法给出（此时不存在『上一次选择』），"
       "种下时会随机变成一张彩卡植物；\\n"
       "· 待选区的模仿者卡始终保持模仿者外观；取消被模仿的植物时，"
       "卡槽里的模仿者卡会一并取消。")
PHS = "它什么都不是，直到你告诉它该成为什么。"

# 内置资源（全部用 res:// 引游戏自带）
BASE_IMITATER_SCENE = "res://Asset/Anime/Character/Plant/Chapter0/Imitater/Scene/TowerDefensePlantImitater.tscn"
BASE_IMITATER_SPRITE = "res://Asset/Anime/Character/Plant/Chapter0/Imitater/Imitater.tscn"
BASE_IMITATER_CSET = "res://Asset/Anime/Character/Plant/Chapter0/Imitater/Scene/TowerDefensePlantImitaterExplodeDefinition.tres"
BASE_PLANT_CSET = "res://Prefab/TowerDefense/Character/ComponentSets/TowerDefensePlantComponentSet.tres"
S_PLANT_CONFIG = "res://Resource/TowerDefense/Character/Config/TowerDefensePlantConfig.cs"
S_CSET = "res://Script/Component/Runtime/CharacterComponentSet.cs"
S_PACKET = "res://Registry/Battle/Feature/PacketBank/Resource/Packet/TowerDefensePacketConfig.cs"

# PACKET_TYPE: NOONE=-1, WHITE=0, GOLD=1, DIAMOND=2, COLOUR=3, STAR=4, ORIGINAL=5, ZOMBIE=6, COVER=7, GRAY=8
PACKET_TYPE_COLOUR = 3

out = []


def w(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    old = None
    if os.path.isfile(path):
        with open(path, "r", encoding="utf-8", newline="") as f:
            old = f.read()
    if old == text:
        return False
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    return True


def log(s):
    out.append(str(s))
    # ★ 实时落盘：这个脚本在 `install()` 阶段有过"挂起"前科（os.rename 被句柄占用），
    #   只在最后统一写盘的话，挂住时**什么都看不到**。这里每条都立刻刷一次。
    try:
        with open(os.path.join(ROOT, "mod", "_imitater_build.log"), "w", encoding="utf-8") as f:
            f.write("\n".join(out))
    except Exception:
        pass


# ---------------- 1) 资源内容 ----------------

def cfg_tres():
    return f"""[gd_resource type="Resource" script_class="TowerDefensePlantConfig" format=3]

[ext_resource type="Script" path="{S_PLANT_CONFIG}" id="1"]

[resource]
script = ExtResource("1")
name = "{CHAR_KEY}"
canCopy = false
damagePointData = null
armorData = null
customData = null
ashScene = null
homeWorld = 1
costRise = -1
cost = 0
packetCooldown = 30.0
plantGridType = [-1]
collisionFlags = 11
maskFlags = 9
metadata/_custom_type_script = "{S_PLANT_CONFIG}"
"""


def cset_tres():
    return f"""[gd_resource type="Resource" script_class="CharacterComponentSet" format=3]

[ext_resource type="Resource" path="{BASE_IMITATER_CSET}" id="1"]
[ext_resource type="Resource" path="{BASE_PLANT_CSET}" id="2"]
[ext_resource type="Script" path="{S_CSET}" id="3"]

[resource]
script = ExtResource("3")
ParentSet = ExtResource("2")
Components = [ExtResource("1")]
"""


def scene_tscn():
    return f"""[gd_scene format=3]

[ext_resource type="PackedScene" path="{BASE_IMITATER_SCENE}" id="1"]
[ext_resource type="Resource" path="../Config/{CFG_FILE}" id="2"]
[ext_resource type="Resource" path="./{CSET_FILE}" id="3"]

[node name="{CHAR_KEY}" instance=ExtResource("1")]
ComponentSet = ExtResource("3")
config = ExtResource("2")
packetBank = "{CUSTOM_BANK}"
metadata/mod_resource_kind = "Character"
metadata/mod_display_name = "{PN}"
metadata/mod_character_category = "Plant"
metadata/mod_character_config_path = "../Config/{CFG_FILE}"
metadata/mod_character_script_path = "./{SCENE_FILE}"
metadata/mod_character_script_binding = "CompanionOnly"
metadata/mod_character_sprite_scene = "../Sprite/{SPRITE_FILE}"
"""


def sprite_tscn():
    return f"""[gd_scene format=3]

[ext_resource type="PackedScene" path="{BASE_IMITATER_SPRITE}" id="1"]

[node name="{CHAR_KEY}" instance=ExtResource("1")]
metadata/mod_resource_kind = "CharacterSprite"
"""


def script_cs():
    """⚠️ **已废弃（v1.0.8），不要调用** —— 保留此函数仅为记录这次踩坑。

    实测报错（v1.0.7）：
        ManifestError: enabled mod 'imitaterclassic' is missing or invalid:
        undeclared executable package file: Resources/.../Script/ImitaterClassic.cs
    ⇒ `ModLoader.IsExecutablePackageFile`（`ModLoader.cs:1316`）的扩展名白名单里
      **确实包含 `.cs`**（还有 `.gd` / `.bat` / `.exe` / `.cmd` / `.ps1`），
      包内出现它就会**整包拒收**。
    ⇒ 正确做法：`mod_character_script_path` 指向**场景文件自己**（`./<Key>.tscn`）——
      引擎只做 `Path.GetFileNameWithoutExtension(path)`，**不读文件内容**。

    ── 以下为原始 docstring ──
    `<Key>/Script/<Key>.cs` —— CompanionOnly 伴随脚本的「声明标记」。

    引擎侧链路（`addons/ModEditor/ModSystem/XWModCharacterCompanionRuntime.cs`
    `TryCreateInstance()`, L160-220）：
      1. 实例化包内角色场景 → `authoredRoot`
      2. 读元数据 `mod_character_script_binding` / `mod_character_script_path`
      3. 要求 `binding == "CompanionOnly"` 且 `path` 非空
      4. `expectedTypeName = Path.GetFileNameWithoutExtension(path)`  → `{CHAR_KEY}`
      5. 在 **Runtime/ModAssembly.dll** 里找 `!IsAbstract && Name == expectedTypeName
         && authoredRoot.GetType().IsAssignableFrom(type)` 的类型
      6. `Activator.CreateInstance` 反射建实例，把壳的**属性 + 子节点**搬过去，丢弃壳

    ⇒ **本文件只被"取文件名"**，内容对引擎无意义；**真正的类在 DLL 里**：
       `public partial class {CHAR_KEY} : TowerDefensePlantImitater {{ }}`（见 ImitaterClassicEntry.cs）。
    ⚠️ 不写这个文件、或元数据对不上，就会退回内置角色
       （表现为"种下去走的是内置植物的技能"，例如模仿者的随机变身）。
    """
    return (
        "// <auto-generated> CompanionOnly 伴随脚本（声明标记，不参与编译）。\n"
        "// 引擎用 Path.GetFileNameWithoutExtension(mod_character_script_path) 取到类名 \"" + CHAR_KEY + "\"，\n"
        "// 再到 Runtime/ModAssembly.dll 里找同名、非抽象、且继承场景根类型的类。\n"
        "// </auto-generated>\n"
    )


def packet_body(cfg_rel):
    return f"""[gd_resource type="Resource" script_class="TowerDefensePacketConfig" format=3]

[ext_resource type="Resource" path="{cfg_rel}" id="1"]
[ext_resource type="Script" path="{S_PACKET}" id="2"]

[resource]
script = ExtResource("2")
saveKey = "{CHAR_KEY}"
unlockCheckList = []
name = "{PN}"
describe = "{PD}"
handbookDescribe = "{PHD}"
handbookStory = "{PHS}"
packetAnimeClip = "Idle"
packetAnimeOffset = Vector2(21, 25)
packetAnimeScale = Vector2(0.5, 0.5)
characterConfig = ExtResource("1")
type = {PACKET_TYPE_COLOUR}
metadata/_custom_type_script = "{S_PACKET}"
"""


def manifest():
    resources = sorted([
        CARD_REL,
        f"{PKG}/Config/{CFG_FILE}",
        f"{PKG}/Packet/{CHAR_KEY}.tres",
        f"{PKG}/Scene/{CSET_FILE}",
        f"{PKG}/Scene/{SCENE_FILE}",
        f"{PKG}/Sprite/{SPRITE_FILE}",
        "Runtime/ModAssembly.dll",
    ], key=lambda p: p.lower())
    return {
        "schemaVersion": 2,
        "id": MOD_ID,
        "name": MOD_NAME,
        "version": "1.5.0",
        "author": "本地",
        "description": (
            f"新增植物「{PN}」（彩卡）：PvZ 1 代原版模仿者行为 —— 选中该卡后自动复制你"
            f"上一次选择的植物种子包，按那张卡的花费与冷却种下，种完自动还原。"
            f"若该卡由随机取卡类玩法给出（无\"上一次选择\"），则随机变成一张彩卡植物。"
            f"（含托管运行时插件 Runtime/ModAssembly.dll）"
        ),
        "dependencies": [],
        "conflicts": [],
        "provides": {
            "Character": [CHAR_KEY],
            "CharacterSprite": [CHAR_KEY],
            "Packet": [CHAR_KEY],
        },
        "overrides": {},
        "scripts": [],
        "runtimeAssembly": "Runtime/ModAssembly.dll",
        "runtimeEntryType": "ImitaterClassicEntry",
        "runtimeApiVersion": 1,
        "runtimeAssemblyPolicy": "optional",
        "blueprints": [],
        "translations": [],
        "resources": resources,
    }


# ---------------- 2) 编译 ----------------

def compile_asm():
    home = os.path.join(ROOT, "tools", "dotnet_home")
    tmpd = os.path.join(home, "tmp")
    nuget = os.path.join(ROOT, "tools", "nuget")
    for d in (home, tmpd, nuget):
        os.makedirs(d, exist_ok=True)
    env = dict(os.environ)
    env["DOTNET_ROOT"] = os.path.join(ROOT, "tools", "dotnet9")
    env["DOTNET_CLI_HOME"] = home
    env["TEMP"] = tmpd
    env["TMP"] = tmpd
    env["TMPDIR"] = tmpd
    env["NUGET_PACKAGES"] = nuget
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    env["MSBUILDDISABLENODEREUSE"] = "1"

    assets = os.path.join(SRC, "obj", "project.assets.json")
    logpath = os.path.join(SRC, "build.log")
    logs = []

    def run(args, timeout):
        p = subprocess.run([DOTNET] + args, cwd=SRC, env=env, capture_output=True,
                           text=True, encoding="utf-8", errors="replace", timeout=timeout)
        logs.append("$ dotnet " + " ".join(args) + "  -> RC=%d" % p.returncode)
        if p.stdout:
            logs.append(p.stdout.strip())
        if p.stderr:
            logs.append(p.stderr.strip())
        return p.returncode

    try:
        subprocess.run(["taskkill", "/F", "/IM", "dotnet.exe"], capture_output=True, timeout=30)
    except Exception:
        pass

    rc = 0
    if not os.path.isfile(assets):
        log("[compile] 首次：先 restore（可能较慢）")
        rc = run(["restore"], 900)
    if rc == 0:
        rc = run(["build", "-c", "Release", "--no-restore",
                  "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false",
                  "-v:q", "-nologo"], 600)

    with open(logpath, "w", encoding="utf-8") as f:
        f.write("\n".join(logs))
    log("[compile] RC=%d，日志 %s" % (rc, logpath))
    return rc == 0


# ---------------- 3) 打包 + 装机 ----------------

def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for c in iter(lambda: f.read(65536), b""):
            h.update(c)
    return h.hexdigest()


def package():
    binsrc = os.path.join(SRC, "bin", "Release", "JTYImitaterClassic.dll")
    if not os.path.isfile(binsrc):
        log("[package] 缺编译产物 " + binsrc)
        return False
    runtime_dir = os.path.join(BASE, "Runtime")
    os.makedirs(runtime_dir, exist_ok=True)
    dll = os.path.join(runtime_dir, "ModAssembly.dll")
    shutil.copyfile(binsrc, dll)
    log("[package] DLL md5=" + md5(dll))

    os.makedirs(os.path.dirname(DIST), exist_ok=True)
    res_root = os.path.join(BASE, "Resources")
    with zipfile.ZipFile(DIST, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(os.path.join(BASE, "mod.json"), "mod.json")
        # ★ 资源必须进包（第一版漏了这一步，包内只剩 mod.json + dll）
        for root, _dirs, files in os.walk(res_root):
            for fn in files:
                fp = os.path.join(root, fn)
                arc = os.path.relpath(fp, BASE).replace("\\", "/")
                z.write(fp, arc)
        z.write(dll, "Runtime/ModAssembly.dll")
    with zipfile.ZipFile(DIST) as z:
        first = z.namelist()[0]          # 写入顺序（mod.json 是第一个写的）
        nl = sorted(z.namelist())
    log("[package] %s  %d B" % (DIST, os.path.getsize(DIST)))
    for n in nl:
        log("          " + n)
    if first != "mod.json" or nl.count("mod.json") != 1:
        log("[package] ❌ mod.json 不在根或不止一个（首个条目=%s）" % first)
        return False
    # 声明与实际必须一致
    declared = sorted(manifest()["resources"], key=lambda p: p.lower())
    actual = sorted([n for n in nl if n != "mod.json"], key=lambda p: p.lower())
    if declared != actual:
        log("[package] ❌ resources 声明与包内实际不一致")
        log("  声明: " + str(declared))
        log("  实际: " + str(actual))
        return False
    log("[package] ✅ resources 声明与实际一致（%d 项）" % len(actual))
    return True


def install():
    dst = os.path.join(MODS, "ImitaterClassic.pmod")
    shutil.copyfile(DIST, dst)
    log("[install] %s (%d B)" % (dst, os.path.getsize(dst)))
    if os.path.isdir(CACHE):
        import time
        ts = time.strftime("%H%M%S")
        for name in os.listdir(CACHE):
            # 只处理"干净"的缓存目录名（不带 .bak），撞名时加序号
            if name == "ImitaterClassic":
                src = os.path.join(CACHE, name)
                dstc = os.path.join(CACHE, name + ".bak_" + ts)
                i = 1
                while os.path.exists(dstc):
                    i += 1
                    dstc = os.path.join(CACHE, "%s.bak_%s_%d" % (name, ts, i))
                # ★ 用系统 `move`（同盘走 MoveFileEx，瞬间完成）。
                #   python 的 `os.rename` / `shutil.move` 在大目录上实测会**挂起**
                #   （目录被扫描/句柄占用）—— 本脚本曾因此卡死 5 分钟无输出。
                try:
                    # `cmd /c move` 的输出是**系统 OEM 编码**（中文 Windows = GBK），
                    # 用 utf-8 解会在 reader 线程抛 UnicodeDecodeError（不致命但很脏）。
                    r = subprocess.run(["cmd", "/c", "move", src, dstc],
                                       capture_output=True, errors="replace",
                                       encoding="gbk", timeout=60)
                    log("[cache] move rc=%d %s" % (
                        r.returncode,
                        ((r.stdout or "") + (r.stderr or "")).strip().replace("\n", " ")))
                except Exception as ex:
                    log("[cache] move 失败（已忽略）：%r" % ex)
    # enabled_mods.json 合并
    en = os.path.join(MODS, "enabled_mods.json")
    try:
        ids = []
        if os.path.isfile(en):
            with open(en, encoding="utf-8") as f:
                ids = json.load(f)
        if MOD_ID not in ids:
            ids.append(MOD_ID)
            with open(en, "w", encoding="utf-8") as f:
                json.dump(ids, f, ensure_ascii=False, indent=2)
            log("[install] enabled_mods.json += " + MOD_ID)
        else:
            log("[install] enabled_mods.json 已含 " + MOD_ID)
    except Exception as e:
        log("[install] enabled_mods.json 合并失败: %r" % e)
    return True


# ---------------- 主流程 ----------------

def main():
    # 1) 生成资源
    files = {
        os.path.join(BASE, PKG, "Config", CFG_FILE): cfg_tres(),
        os.path.join(BASE, PKG, "Scene", CSET_FILE): cset_tres(),
        os.path.join(BASE, PKG, "Scene", SCENE_FILE): scene_tscn(),
        os.path.join(BASE, PKG, "Sprite", SPRITE_FILE): sprite_tscn(),
        os.path.join(BASE, PKG, "Packet", CHAR_KEY + ".tres"): packet_body(f"../Config/{CFG_FILE}"),
        # ⚠️ v1.0.8 起**不再生成** `Script/<Key>.cs`：
        #   `ModLoader.IsExecutablePackageFile`（ModLoader.cs:1316）的扩展名白名单里
        #   **包含 `.cs`**（还有 `.gd` / `.bat` / `.exe` / `.cmd` / `.ps1`）⇒ 包内出现它就会被判
        #   `undeclared executable package file`、**整包拒收**（v1.0.7 实测报错）。
        #   而 `mod_character_script_path` 其实**只被取文件名**（引擎不读文件内容），
        #   所以直接指向**场景文件自己**（`./<Key>.tscn`）即可，类名同样是 `<Key>`。
        os.path.join(BASE, CARD_REL.replace("/", os.sep)):
            packet_body(f"../Characters/Plants/{CHAR_KEY}/Config/{CFG_FILE}"),
    }
    for p, t in files.items():
        ch = w(p, t)
        log("[gen] %s %s" % ("updated" if ch else "same   ", os.path.relpath(p, BASE)))

    w(os.path.join(BASE, "mod.json"),
      json.dumps(manifest(), ensure_ascii=False, indent=2) + "\n")
    log("[gen] mod.json")

    # 2) 编译
    if not compile_asm():
        log("❌ 编译失败，终止")
        return 1

    # 3) 打包 + 装机
    if not package():
        return 1
    if "--install" in sys.argv:
        install()
    return 0


if __name__ == "__main__":
    rc = 0
    try:
        rc = main()
    except Exception as e:
        import traceback
        log("EXC: %r" % e)
        log(traceback.format_exc())
        rc = 1
    txt = "\n".join(out)
    with open(os.path.join(ROOT, "mod", "_imitater_build.log"), "w", encoding="utf-8") as f:
        f.write(txt)
    print(txt)
    sys.exit(rc)
