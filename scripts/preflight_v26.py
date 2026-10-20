#!/usr/bin/env python3
"""Checks for the 7 Days to Die 2.6 Bestiary release, no game launch required.

Static checks do not prove player-session or Rebirth combat behavior.
Use --package after creating the release ZIP.
"""
import argparse
import re
import sys
import urllib.request
import zipfile
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
BUTTON_REL = Path("assets/1_BSG_Bestiario_UI/Config/XUi/windows.xml")
MODINFO_REL = Path("assets/1_BSG_Bestiario_UI/ModInfo.xml")
VANILLA_URL = ("https://raw.githubusercontent.com/Insanity404/"
               "7d2d-assets/main/v2.6/Config/XUi/windows.xml")
ERRORS = []


def test(name, ok, details=""):
    print(("PASS" if ok else "FAIL") + " | " + name + (" | " + details if details else ""))
    if not ok:
        ERRORS.append(name)


def validate_xml(filename):
    try:
        return ET.parse(filename).getroot()
    except (OSError, ET.ParseError) as exc:
        ERRORS.append("Invalid XML: " + str(filename))
        print("FAIL | XML invalid: " + str(filename) + " | " + str(exc))
        return None


def validate_button(root):
    path = BUTTON_REL
    xml = validate_xml(ROOT / path)
    if xml is None:
        return
    test("modlet uses 2.6 XUi directory", path.parent.name == "XUi")
    test("no 3.0 XUi_InGame in distributed modlet",
         not (ROOT / "assets/1_BSG_Bestiario_UI/Config/XUi_InGame").exists())
    append_nodes = xml.findall("append")
    test("single append patch", len(append_nodes) == 1)
    if not append_nodes:
        return
    node = append_nodes[0]
    expected = "/windows/window[@name='CharacterFrameWindow']"
    test("CharacterFrameWindow 2.6 root XPath", node.get("xpath") == expected)
    controllers = node.findall("rect")
    test("one named Bestiary clickable controller",
         len(controllers) == 1 and
         controllers[0].get("name") == "bsgBestiaryButton" and
         controllers[0].get("controller") == "SimpleButton")
    if controllers:
        btn = controllers[0]
        clickable = btn.find("button")
        test("native clickable child exists", clickable is not None and
             clickable.get("name") == "clickable" and
             clickable.get("caption") == "B")
        test("mouse cursor area enabled", btn.get("cursor_area") == "true")
        try:
            posx = int(btn.get("pos", "0,0").split(",")[0])
            width = int(btn.get("width", "0"))
            posy = int(btn.get("pos", "0,0").split(",")[1])
            depth = int(btn.get("depth", "0"))
            # CharacterFrameWindow width 327: -76 estaba fuera y el click
            # no alcanzaba el controlador, aunque el tooltip funcionara.
            test("Bestiary clickable rect inside native CharacterFrameWindow",
                 posx == 252 and width == 34 and posx >= 0 and posx + width <= 327,
                 "B: " + str(posx) + ".." + str(posx + width))
            # ptTitlesPanel depth=20, header at y=-46; close button x=291.
            test("Bestiary button over Titles panel, clear of its close button",
                 depth > 20 and posy == -49 and posx + width < 291,
                 "depth=" + str(depth) + " y=" + str(posy))
        except (ValueError, IndexError):
            test("button geometry numeric", False)


def verify_vanilla_xpath(local_xml=None):
    try:
        if local_xml:
            root = ET.parse(local_xml).getroot()
        else:
            with urllib.request.urlopen(VANILLA_URL, timeout=25) as response:
                root = ET.fromstring(response.read())
        elements = root.findall("./window[@name='CharacterFrameWindow']/panel[@name='header']")
        test("XPath matches actual Vanilla v2.6 windows.xml", len(elements) == 1)
        window = root.find("./window[@name='CharacterFrameWindow']")
        test("Vanilla v2.6 character window exists and has width=327",
             window is not None and window.get("width") == "327")
    except Exception as exc:
        test("Vanilla v2.6 reference downloaded and parsed", False, str(exc))


def verify_source():
    expansion = (ROOT / "src/BsgBestiaryExpansion.cs").read_text(encoding="utf-8")
    chronicle = (ROOT / "src/BsgChronicleV07.cs").read_text(encoding="utf-8")
    test("no hardcoded F8 or any keyboard shortcut in bestiary",
         "KeyCode.F8" not in expansion and "Input.GetKey" not in expansion)
    test("world entityId for deduplication",
         "int id = victim.entityId;" in expansion)
    test("bounded logging for accepted kills", "LoggedKills <= 12" in expansion)
    test("button subscribed to native XUi press",
         'GetChildById("bsgBestiaryButton")' in chronicle and
         "boundSimple.OnPressed += HandlePress;" in chronicle)
    test("XML repair keeps old title data in backup",
         ".bsg_antes_de_reparar.bak" in chronicle)
    test("family catalog dynamically populated from editable XML",
         "public static List<BestiaryFamilyDefinition> GetFamilies()" in chronicle)
    test("XML class names used instead of CLR names",
         "victim.EntityClass" in expansion and
         "info.entityClassName" in expansion and
         "return victim.GetType().Name;" not in expansion)
    test("conservative migration of old one-kill demolition",
         "RecoverLegacyDemolisher()" in expansion and
         "old.Kills != 1" in expansion and
         'new XAttribute("schema", "3")' in expansion)
    test("soldier family backfill from saved XML species", 
         'RecoverSoldierKills()' in expansion and
         'StartsWith("zombieSoldier"' in expansion and
         'new XAttribute("schema", "3")' in expansion)
    soldier_category = ROOT / "assets/0_BSG_Bestiario_Soldados_CATEGORY.xml"
    test("soldier category XML template present", soldier_category.is_file())
    if soldier_category.is_file():
        cat = ET.parse(soldier_category).getroot()
        titles = [t.get("text") for t in cat.findall("tier")]
        test("soldier category matches all zombieSoldier variants",
             cat.get("id") == "bsg_soldiers" and
             cat.get("kill_entity") == "zombieSoldier" and
             len(titles) == 4 and len(set(titles)) == 4)
    test("automatic tier metadata upgrade preserves titles and backs up existing XML",
         "EnsureMasteryTierMetadata(path);" in chronicle and
         ".bsg_before_masteries_v033.bak" in chronicle and
         "Math.Min(i, 2)" in chronicle and
         "buffBSG_" in chronicle)
    buffconfig = ROOT / "assets/BSG_Masteries_Config/buffs.xml"
    tagsconfig = ROOT / "assets/zzzzzzzz_BSG_Masteries_Tags/Config/entityclasses.xml"
    for config in (buffconfig, tagsconfig):
        test("mastery XML exists: " + config.name, config.is_file())
        if config.is_file():
            validate_xml(config)
    if buffconfig.is_file() and tagsconfig.is_file():
        b = ET.parse(buffconfig).getroot()
        e = ET.parse(tagsconfig).getroot()
        buffs = b.findall(".//buff")
        test("48 distinct family/rank mastery buffs", len(buffs) == 48 and
             len({x.get("name") for x in buffs}) == 48)
        test("mastery includes 10/20/25 damage and 5/10 dismember",
             {x.get("value") for x in b.findall(".//passive_effect[@name='EntityDamage']")} == {"0.10","0.20","0.25"} and
             {x.get("value") for x in b.findall(".//passive_effect[@name='DismemberChance']")} == {"0.05","0.10"})
        tags = e.findall("append")
        test("48 disjoint XML class target tags", len(tags) == 48 and
             len({x.text for x in tags}) == 48)
        test("target tags assigned to existing entity Tags properties",
             all(x.get("xpath", "").endswith("/property[@name='Tags']/@value") for x in tags))
        test("buff targets carry exact signature requirement",
             all(g.find("requirement[@name='EntityTagCompare'][@target='other']") is not None
                 for g in b.findall(".//effect_group")))
        test("overlap exclusivity uses NotHasBuff",
             len(b.findall(".//requirement[@name='NotHasBuff']")) >= 30)
    test("Chronicle retains 4 unlock records per world",
         'bsg_chronicle_history.xml' in chronicle and
         'private static void EnsureHistoryLoaded()' in chronicle and
         'SaveHistory();' in chronicle and
         'MaxHistory = 4' in chronicle)
    test("Chronicle historical title recovery is labeled as old",
         'BackfillFromPriorUnlocks' in chronicle and
         'RememberRecoveredTitle' in chronicle and
         'ya había desbloqueado' in chronicle)
    test("active native PlayerTitles reward sync",
         'SyncNativeActiveTitle' in chronicle and
         'GetChildById("ptActiveLabel")' in chronicle)
    test("native title notification preserved if Chronicle cannot handle it",
         'bool processed = PlayerTitlesBridge.OnNotify' in chronicle and
         'return true;' in chronicle)
    test("AddBuff activation status logged",
         'buff no activo despues de AddBuff' in chronicle)
    mmo = ROOT / "src/BsgMMOBestiaryWindow.cs"
    hover = ROOT / "src/PlayerTitlesTooltipBridge.cs"
    test("new MMO bestiary window source exists", mmo.is_file())
    test("title hover bridge source exists", hover.is_file())
    if mmo.is_file():
        ui = mmo.read_text(encoding="utf-8")
        test("MMO overlay autohides when character panel is closed",
             "CharacterScreenVisible()" in ui and
             "Auto-cierre: ventana de Personaje cerrada." in ui and
             "opened = false;" in ui)
        test("MMO UI does not show XML class IDs or old debug labels",
             "creature.ClassName" not in ui and
             "Clase objetivo" not in ui and
             "v0.23" not in ui and
             "v0.21" not in ui)
        test("MMO UI includes images, collection and mastery cards",
             'GetPortrait(family.Id)' in ui and
             'CRIATURAS DESCUBIERTAS' in ui and
             'RECOMPENSAS Y TÍTULOS' in ui)
        test("MMO UI keeps Radiated vs Tainted separate",
             '"bsg_radiated"' in ui and '"bsg_tainted"' in ui)
    if hover.is_file():
        source = hover.read_text(encoding="utf-8")
        test("hover bridge displays requirements and bonus",
             "ChronicleCatalog.BuildTooltip" in source and
             "row.ViewComponent.ToolTip = tooltip;" in source and
             "ptRow" in source)
    test("MMO and hover bridge connected through original B button",
         "BsgMMOBestiaryWindow.Toggle();" in expansion and
         "PlayerTitlesTooltipBridge.Tick();" in chronicle and
         "boundSimple.OnPressed += HandlePress;" in chronicle)
    test("no direct unsupported RemoveBuff calls",
         ".Buffs.RemoveBuff(" not in chronicle)


def verify_package(path):
    try:
        with zipfile.ZipFile(path) as z:
            bad = z.testzip()
            test("ZIP CRC valid", bad is None, str(bad or ""))
            names = [n.replace("\\", "/") for n in z.namelist()]
            required = [
                "0_BSG_Bestiario/bsg_BestiaryChronicle.dll",
                "0_BSG_Bestiario/Config/buffs.xml",
                "zzzzzzzz_BSG_Masteries_Tags/ModInfo.xml",
                "zzzzzzzz_BSG_Masteries_Tags/Config/entityclasses.xml",
                "1_BSG_Bestiario_UI/Config/XUi/windows.xml",
                "1_BSG_Bestiario_UI/ModInfo.xml",
            ]
            for needed in required:
                test("ZIP includes " + needed, needed in names)
            test("ZIP excludes 3.0 XUi_InGame patches",
                 not any("XUi_InGame/" in n for n in names))
            test("ZIP does not overwrite user playertitles.xml",
                 not any(n.endswith("Config/playertitles.xml") for n in names))
            test("ZIP retains correct root mod folders",
                 not any(n.startswith(("package/", "assets/")) for n in names))
    except (OSError, zipfile.BadZipFile) as exc:
        test("ZIP readable", False, str(exc))


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--package", help="path to finished install ZIP")
    p.add_argument("--vanilla-windows", help="local Vanilla v2.6 windows.xml")
    args = p.parse_args()
    for rel in (BUTTON_REL, MODINFO_REL):
        test("exists " + str(rel), (ROOT / rel).exists())
    validate_xml(ROOT / MODINFO_REL)
    validate_button(ROOT)
    verify_source()
    verify_vanilla_xpath(args.vanilla_windows)
    if args.package:
        verify_package(args.package)
    print("\nTOTAL: " + ("PASS" if not ERRORS else "FAIL: " + ", ".join(ERRORS)))
    return 1 if ERRORS else 0


if __name__ == "__main__":
    sys.exit(main())
