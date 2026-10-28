#!/usr/bin/env bash
# Add the decompiled reference trees to the main solution for IDE browsing.
# Run scripts/decompile-game.sh first; this only wires up project files.
#
# Output:
#   reference/Directory.Build.props         # shared build settings + warning noise
#   main-solution entries: a solution folder "reference" holding both decompiled
#     projects, build-disabled (ActiveCfg entries only, no Build.0), so building
#     the main solution never compiles them. The sln is committed, the project
#     files are gitignored: run this script after a fresh clone too, or the IDE
#     shows the two entries as missing.
#   patched csproj files (idempotent):
#     net40 -> netstandard2.1 target
#     Assembly-CSharp: binary Firstpass reference replaced by a ProjectReference,
#     so go-to-definition lands in decompiled source instead of the game DLL.
#
# Re-apply after a fresh full decompile (decompiler artifacts the C# compiler rejects;
# Mono's game build compiled them fine):
#   firstpass/Amplitude.Mercury.AI/AIController.cs              Brain.ExecutionInfo /
#     Brain.ActuatorInfo -> fully qualify Amplitude.Mercury.AI.Brain.Brain.*
#   firstpass/Amplitude.Mercury.Interop/LocalizationManager.cs  AssetBundleManifest ->
#     fully qualify Amplitude.Framework.Asset.*
#   firstpass/Amplitude.Mercury.Interop/EmpireNameSnapshot.cs   GetEmpireCultureNameParameter
#     ref-local + goto pattern -> plain ref conditional
#   firstpass/Amplitude.Mercury/SaveHelper.cs                   add local function
#     Read(fileName) reading readHandle.OpenDataRead into memoryStream
#   outgame/Amplitude.Mercury.Audio/ListenerPositioningAudioModule.cs  AudioListener ->
#     fully qualify UnityEngine.*
#   outgame/Amplitude.Mercury.Avatar/AvatarId.cs                move mask init from
#     phantom field initializer into AvatarId(int index)
#   outgame/Amplitude.Mercury.UI/MapsList_Item.cs               ExpandToggle_Switch
#     ref-escape pattern -> plain null check
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${OUT:-$REPO_ROOT/reference/managed}"
OUTGAME_DIR="${OUTGAME_DIR:-$OUT/../outgame}"
FP_PROJ="$OUT/firstpass/Amplitude.Mercury.Firstpass.csproj"
OC_PROJ="$OUTGAME_DIR/assembly-csharp/Assembly-CSharp.csproj"
SLN="$REPO_ROOT/Humankind-SettlementPlacementAdvisor.sln"
PROPS="$REPO_ROOT/reference/Directory.Build.props"

[ -f "$FP_PROJ" ] || { echo "missing $FP_PROJ; run scripts/decompile-game.sh first" >&2; exit 1; }
[ -f "$OC_PROJ" ] || { echo "missing $OC_PROJ; run scripts/decompile-game.sh first" >&2; exit 1; }

# netstandard2.1 reference assemblies carry the .NET Core 2.x APIs the game's Mono 5
# runtime provides (MathF, Array.Fill, Dictionary.TryAdd, System.Index/ranges) and
# System.Net.Http. NoWarn silences decompiler-output noise. Applies to both projects.
cat > "$PROPS" <<'EOF'
<Project>
  <PropertyGroup>
    <NoWarn>$(NoWarn);1591;1701;1702;1700;0618;0067;0169;0649;0436</NoWarn>
    <GenerateDependencyFile>false</GenerateDependencyFile>
  </PropertyGroup>
</Project>
EOF

python3 - "$FP_PROJ" "$OC_PROJ" <<'PY'
import os, re, sys

fp_proj, oc_proj = sys.argv[1], sys.argv[2]

for path in (fp_proj, oc_proj):
    text = open(path).read()
    for old in ("net40", "net472"):
        text = text.replace(f"<TargetFramework>{old}</TargetFramework>",
                            "<TargetFramework>netstandard2.1</TargetFramework>")
    open(path, "w").write(text)

text = open(oc_proj).read()
if "ProjectReference" not in text:
    text = re.sub(
        r'[ \t]*<Reference Include="Amplitude\.Mercury\.Firstpass">\s*'
        r'<HintPath>.*?</HintPath>\s*</Reference>\r?\n',
        '', text, count=1, flags=re.S)
    rel = os.path.relpath(fp_proj, os.path.dirname(oc_proj))
    text = text.replace(
        "</Project>",
        '  <ItemGroup>\n'
        f'    <ProjectReference Include="{rel}" />\n'
        '  </ItemGroup>\n</Project>', 1)
    open(oc_proj, "w").write(text)

# Strip Mono framework DLLs from ilspy's references (mscorlib/System.* in the game
# folder type-forward into the game mscorlib and clash with netstandard references);
# then add every game DLL neither project already references.
FRAMEWORK = re.compile(r"(mscorlib|netstandard|System([.].*)?|Mono([.].*)?|I18N([.].*)?|Microsoft([.].*)?)")
for proj in (fp_proj, oc_proj):
    text = open(proj).read()
    def drop(m):
        return "" if FRAMEWORK.fullmatch(m.group(1)) else m.group(0)
    text = re.sub(r'[ \t]*<Reference Include="([^"]+)">\s*<HintPath>.*?</HintPath>\s*</Reference>\r?\n',
                  drop, text, flags=re.S)
    open(proj, "w").write(text)

hint = re.search(r'<HintPath>([^<]+)</HintPath>', open(fp_proj).read())
managed = os.path.dirname(hint.group(1))
dlls = sorted(f[:-4] for f in os.listdir(managed) if f.endswith(".dll"))
compiled = {"Amplitude.Mercury.Firstpass", "Assembly-CSharp"}  # source, not binary

for proj in (fp_proj, oc_proj):
    text = open(proj).read()
    have = set(re.findall(r'<Reference Include="([^"]+)"', text))
    missing = [d for d in dlls
               if d not in have and d not in compiled and not FRAMEWORK.fullmatch(d)]
    if not missing:
        continue
    refs = "".join(
        f'    <Reference Include="{d}">'
        f'<HintPath>{os.path.join(managed, d)}.dll</HintPath></Reference>\n'
        for d in missing)
    text = text.replace("</Project>",
                        "  <ItemGroup>\n" + refs + "  </ItemGroup>\n</Project>", 1)
    open(proj, "w").write(text)
PY

# Main-solution entries, idempotent on the fixed GUIDs. Build stays disabled:
# every solution config gets an ActiveCfg entry per project and no Build.0 line,
# so building the main solution compiles only plugin/tests/CfgGen while the IDE
# still loads the decompiled trees for browsing. The sln is committed and the
# project files are gitignored; on a fresh clone the entries show as missing
# until scripts/decompile-game.sh has run.
rm -f "$REPO_ROOT/reference/Explorer.sln"  # superseded by the main-solution entries
python3 - "$SLN" <<'PY'
import re, sys

sln_path = sys.argv[1]
text = open(sln_path, encoding="utf-8-sig").read()
nl = "\r\n" if "\r\n" in text else "\n"
FOLDER = ("reference", "{3F0A6C41-9A1B-4E6D-9B62-1C5D47E7A003}")
PROJECTS = [
    ("Amplitude.Mercury.Firstpass",
     r"reference\managed\firstpass\Amplitude.Mercury.Firstpass.csproj",
     "{3F0A6C41-9A1B-4E6D-9B62-1C5D47E7A001}"),
    ("Assembly-CSharp",
     r"reference\outgame\assembly-csharp\Assembly-CSharp.csproj",
     "{3F0A6C41-9A1B-4E6D-9B62-1C5D47E7A002}"),
]
if FOLDER[1] in text:
    # Fresh decompile re-runs this script for the csproj patches; the entries persist.
    print("main-solution entries already present; sln untouched")
    sys.exit(0)

# Every configuration of the main solution gets an ActiveCfg entry (no Build.0).
sec = re.search(r"GlobalSection\(SolutionConfigurationPlatforms\) = preSolution\n(.*?)EndGlobalSection", text, re.S)
configs = [m.group(1) for s in sec.group(1).splitlines()
           for m in [re.match(r"^(.+) = (.+)$", s.strip())]
           if m and m.group(1) == m.group(2)]
if not configs:
    sys.exit("no solution configurations found")

# Project + folder entries go right before the Global section.
entries = f'Project("{{2150E333-8FDC-42A3-9474-1A3956D46DE8}}") = "{FOLDER[0]}", "{FOLDER[0]}", "{FOLDER[1]}"{nl}EndProject{nl}'
for name, path, guid in PROJECTS:
    entries += f'Project("{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}") = "{name}", "{path}", "{guid}"{nl}EndProject{nl}'
pos = re.search(r"^Global\s*$", text, re.M).start()
text = text[:pos] + entries + text[pos:]

# ActiveCfg-only platform entries.
sec = re.search(r"GlobalSection\(ProjectConfigurationPlatforms\) = postSolution\n(.*?)EndGlobalSection", text, re.S)
added = "".join(f"\t\t{guid}.{cfg}.ActiveCfg = {cfg}{nl}"
                for _, _, guid in PROJECTS for cfg in configs)
# The capture ends with the EndGlobalSection line's leading tabs: insert before them.
pos = sec.start(1) + len(sec.group(1).rstrip("\t"))
text = text[:pos] + added + text[pos:]

# Nest both projects under the solution folder.
added = "".join(f"\t\t{guid} = {FOLDER[1]}{nl}" for _, _, guid in PROJECTS)
sec = re.search(r"GlobalSection\(NestedProjects\) = preSolution\n(.*?)EndGlobalSection", text, re.S)
if sec:
    pos = sec.start(1) + len(sec.group(1).rstrip("\t"))
    text = text[:pos] + added + text[pos:]
else:
    text = text.replace("EndGlobal" + nl,
                        f"\tGlobalSection(NestedProjects) = preSolution{nl}" + added +
                        f"\tEndGlobalSection{nl}" + "EndGlobal" + nl, 1)

open(sln_path, "w", encoding="utf-8-sig", newline="").write(text)
PY

echo "==> Main solution updated: $SLN"
echo "Open the main solution: the reference folder holds both decompiled trees for completion and navigation, build-disabled."
echo "Build check: dotnet build \"$SLN\" (the reference projects stay unbuilt)"
