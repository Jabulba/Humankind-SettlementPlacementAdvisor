#!/usr/bin/env bash
# Generate an IDE-browsable solution over the decompiled reference trees.
# Run scripts/decompile-game.sh first; this only wires up project files.
#
# Output (all inside the gitignored reference/ tree):
#   reference/Explorer.sln                  # opens both trees in one workspace
#   reference/Directory.Build.props         # shared build settings + warning noise
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
SLN="$REPO_ROOT/reference/Explorer.sln"
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

cat > "$SLN" <<'EOF'
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Amplitude.Mercury.Firstpass", "managed\firstpass\Amplitude.Mercury.Firstpass.csproj", "{3F0A6C41-9A1B-4E6D-9B62-1C5D47E7A001}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Assembly-CSharp", "outgame\assembly-csharp\Assembly-CSharp.csproj", "{3F0A6C41-9A1B-4E6D-9B62-1C5D47E7A002}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{3F0A6C41-9A1B-4E6D-9B62-1C5D47E7A001}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{3F0A6C41-9A1B-4E6D-9B62-1C5D47E7A001}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{3F0A6C41-9A1B-4E6D-9B62-1C5D47E7A001}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{3F0A6C41-9A1B-4E6D-9B62-1C5D47E7A001}.Release|Any CPU.Build.0 = Release|Any CPU
		{3F0A6C41-9A1B-4E6D-9B62-1C5D47E7A002}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{3F0A6C41-9A1B-4E6D-9B62-1C5D47E7A002}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{3F0A6C41-9A1B-4E6D-9B62-1C5D47E7A002}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{3F0A6C41-9A1B-4E6D-9B62-1C5D47E7A002}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
EOF

echo "==> Solution: $SLN"
echo "Open Explorer.sln (or Explorer.csproj's Assembly-CSharp project) for completion and navigation."
echo "Build check: dotnet build \"$SLN\""
