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
    buttons = node.findall("button")
    test("one Bestiary button", len(buttons) == 1 and
         buttons[0].get("name") == "bsgBestiaryButton")
    if buttons:
        btn = buttons[0]
        try:
            posx = int(btn.get("pos", "0,0").split(",")[0])
            width = int(btn.get("width", "0"))
            # Títulos usa pos=-38,width=34; B tiene que quedar a su izquierda.
            test("Bestiary B is adjacent to existing Titles T", 
                 posx == -76 and width == 34 and posx + width <= -38,
                 "BSG B: " + str(posx) + ".." + str(posx + width))
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
         "bound.OnPress += HandlePress;" in chronicle)
    test("XML repair keeps old title data in backup",
         ".bsg_antes_de_reparar.bak" in chronicle)
    test("15-family catalog populated from editable XML",
         "public static List<BestiaryFamilyDefinition> GetFamilies()" in chronicle)
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
