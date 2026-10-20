#!/usr/bin/env python3
"""Generates LockTune.xcodeproj/project.pbxproj from the files on disk."""
import hashlib, os, re, sys

root = sys.argv[1]
name = "LockTune"
src_dir = os.path.join(root, name)

def uid(*parts):
    return hashlib.md5("/".join(parts).encode()).hexdigest()[:24].upper()

def q(v):
    v = str(v)
    if v and re.fullmatch(r"[A-Za-z0-9_./]+", v):
        return v
    return '"' + v.replace("\\", "\\\\").replace('"', '\\"') + '"'

# Collect files grouped by folder.
groups = {}
for dirpath, dirs, files in os.walk(src_dir):
    rel = os.path.relpath(dirpath, src_dir)
    # Asset catalogs are added as a single item, not walked into.
    catalogs = [d for d in dirs if d.endswith(".xcassets")]
    dirs[:] = [d for d in dirs if not d.endswith(".xcassets")]
    for f in sorted(list(files) + catalogs):
        if f.startswith("."): continue
        groups.setdefault(rel, []).append(f)

ftype = {".xcassets": "folder.assetcatalog", ".swift": "sourcecode.swift", ".plist": "text.plist.xml", ".entitlements": "text.plist.entitlements"}

file_refs, build_files, sources, resources = [], [], [], []
group_children = {}
for rel in sorted(groups):
    for f in groups[rel]:
        path = f if rel == "." else f"{rel}/{f}"
        ref = uid("ref", path)
        ext = os.path.splitext(f)[1]
        file_refs.append(f"\t\t{ref} /* {f} */ = {{isa = PBXFileReference; lastKnownFileType = {ftype.get(ext, 'text')}; path = {q(f)}; sourceTree = \"<group>\"; }};")
        group_children.setdefault(rel, []).append((ref, f))
        if ext == ".swift":
            bf = uid("bf", path)
            build_files.append(f"\t\t{bf} /* {f} in Sources */ = {{isa = PBXBuildFile; fileRef = {ref} /* {f} */; }};")
            sources.append((bf, f))
        elif ext == ".xcassets":
            bf = uid("bf", path)
            build_files.append(f"\t\t{bf} /* {f} in Resources */ = {{isa = PBXBuildFile; fileRef = {ref} /* {f} */; }};")
            resources.append((bf, f))

PROJECT = uid("project"); MAIN_GROUP = uid("maingroup"); PRODUCTS = uid("products")
APP_GROUP = uid("group", name); PRODUCT_REF = uid("product")
TARGET = uid("target"); SRC_PHASE = uid("sources"); FW_PHASE = uid("frameworks"); RES_PHASE = uid("resources")
PROJ_CL = uid("proj-cl"); TGT_CL = uid("tgt-cl")
PROJ_DEBUG = uid("proj-debug"); PROJ_RELEASE = uid("proj-release")
TGT_DEBUG = uid("tgt-debug"); TGT_RELEASE = uid("tgt-release")

group_objs = []
sub_ids = []
for rel in sorted(k for k in group_children if k != "."):
    gid = uid("group", rel)
    sub_ids.append((gid, rel))
    kids = "\n".join(f"\t\t\t\t{r} /* {f} */," for r, f in group_children[rel])
    group_objs.append(f"\t\t{gid} /* {rel} */ = {{\n\t\t\tisa = PBXGroup;\n\t\t\tchildren = (\n{kids}\n\t\t\t);\n\t\t\tpath = {q(rel)};\n\t\t\tsourceTree = \"<group>\";\n\t\t}};")
app_kids = "\n".join(f"\t\t\t\t{g} /* {r} */," for g, r in sub_ids)
group_objs.append(f"\t\t{APP_GROUP} /* {name} */ = {{\n\t\t\tisa = PBXGroup;\n\t\t\tchildren = (\n{app_kids}\n\t\t\t);\n\t\t\tpath = {name};\n\t\t\tsourceTree = \"<group>\";\n\t\t}};")
group_objs.append(f"\t\t{MAIN_GROUP} = {{\n\t\t\tisa = PBXGroup;\n\t\t\tchildren = (\n\t\t\t\t{APP_GROUP} /* {name} */,\n\t\t\t\t{PRODUCTS} /* Products */,\n\t\t\t);\n\t\t\tsourceTree = \"<group>\";\n\t\t}};")
group_objs.append(f"\t\t{PRODUCTS} /* Products */ = {{\n\t\t\tisa = PBXGroup;\n\t\t\tchildren = (\n\t\t\t\t{PRODUCT_REF} /* {name}.app */,\n\t\t\t);\n\t\t\tname = Products;\n\t\t\tsourceTree = \"<group>\";\n\t\t}};")

def settings_block(d):
    lines = []
    for k in sorted(d):
        v = d[k]
        if isinstance(v, list):
            items = "".join(f"\n\t\t\t\t\t{q(x)}," for x in v)
            lines.append(f"\t\t\t\t{k} = ({items}\n\t\t\t\t);")
        else:
            lines.append(f"\t\t\t\t{k} = {q(v)};")
    return "\n".join(lines)

common_proj = {
    "ALWAYS_SEARCH_USER_PATHS": "NO",
    "CLANG_ENABLE_MODULES": "YES",
    "CLANG_ENABLE_OBJC_ARC": "YES",
    "ENABLE_STRICT_OBJC_MSGSEND": "YES",
    "GCC_C_LANGUAGE_STANDARD": "gnu17",
    "MACOSX_DEPLOYMENT_TARGET": "14.0",
    "SDKROOT": "macosx",
    "SWIFT_VERSION": "5.0",
}
proj_debug = dict(common_proj, **{
    "DEBUG_INFORMATION_FORMAT": "dwarf",
    "ENABLE_TESTABILITY": "YES",
    "GCC_OPTIMIZATION_LEVEL": "0",
    "GCC_PREPROCESSOR_DEFINITIONS": ["DEBUG=1", "$(inherited)"],
    "ONLY_ACTIVE_ARCH": "YES",
    "SWIFT_ACTIVE_COMPILATION_CONDITIONS": "DEBUG",
    "SWIFT_OPTIMIZATION_LEVEL": "-Onone",
})
proj_release = dict(common_proj, **{
    "DEBUG_INFORMATION_FORMAT": "dwarf-with-dsym",
    "ENABLE_NS_ASSERTIONS": "NO",
    "SWIFT_COMPILATION_MODE": "wholemodule",
    "SWIFT_OPTIMIZATION_LEVEL": "-O",
})
tgt = {
    "ASSETCATALOG_COMPILER_APPICON_NAME": "AppIcon",
    "CODE_SIGN_ENTITLEMENTS": f"{name}/Support/{name}.entitlements",
    "CODE_SIGN_IDENTITY": "-",
    "CODE_SIGN_STYLE": "Automatic",
    "COMBINE_HIDPI_IMAGES": "YES",
    "CURRENT_PROJECT_VERSION": "1",
    "DEVELOPMENT_TEAM": "",
    "ENABLE_HARDENED_RUNTIME": "YES",
    "GENERATE_INFOPLIST_FILE": "NO",
    "INFOPLIST_FILE": f"{name}/Support/Info.plist",
    "LD_RUNPATH_SEARCH_PATHS": ["$(inherited)", "@executable_path/../Frameworks"],
    "MACOSX_DEPLOYMENT_TARGET": "14.0",
    "MARKETING_VERSION": "1.0",
    "PRODUCT_BUNDLE_IDENTIFIER": "com.example.LockTune",
    "PRODUCT_NAME": "$(TARGET_NAME)",
    "SWIFT_EMIT_LOC_STRINGS": "NO",
    "SWIFT_VERSION": "5.0",
}

def config(cid, cname, d):
    return f"\t\t{cid} /* {cname} */ = {{\n\t\t\tisa = XCBuildConfiguration;\n\t\t\tbuildSettings = {{\n{settings_block(d)}\n\t\t\t}};\n\t\t\tname = {cname};\n\t\t}};"

res_list = "\n".join(f"\t\t\t\t{b} /* {f} in Resources */," for b, f in resources)
src_list = "\n".join(f"\t\t\t\t{b} /* {f} in Sources */," for b, f in sources)

out = f"""// !$*UTF8*$!
{{
\tarchiveVersion = 1;
\tclasses = {{
\t}};
\tobjectVersion = 56;
\tobjects = {{

/* Begin PBXBuildFile section */
{chr(10).join(build_files)}
/* End PBXBuildFile section */

/* Begin PBXFileReference section */
\t\t{PRODUCT_REF} /* {name}.app */ = {{isa = PBXFileReference; explicitFileType = wrapper.application; includeInIndex = 0; path = {name}.app; sourceTree = BUILT_PRODUCTS_DIR; }};
{chr(10).join(file_refs)}
/* End PBXFileReference section */

/* Begin PBXFrameworksBuildPhase section */
\t\t{FW_PHASE} /* Frameworks */ = {{
\t\t\tisa = PBXFrameworksBuildPhase;
\t\t\tbuildActionMask = 2147483647;
\t\t\tfiles = (
\t\t\t);
\t\t\trunOnlyForDeploymentPostprocessing = 0;
\t\t}};
/* End PBXFrameworksBuildPhase section */

/* Begin PBXGroup section */
{chr(10).join(group_objs)}
/* End PBXGroup section */

/* Begin PBXNativeTarget section */
\t\t{TARGET} /* {name} */ = {{
\t\t\tisa = PBXNativeTarget;
\t\t\tbuildConfigurationList = {TGT_CL} /* Build configuration list for PBXNativeTarget "{name}" */;
\t\t\tbuildPhases = (
\t\t\t\t{SRC_PHASE} /* Sources */,
\t\t\t\t{FW_PHASE} /* Frameworks */,
\t\t\t\t{RES_PHASE} /* Resources */,
\t\t\t);
\t\t\tbuildRules = (
\t\t\t);
\t\t\tdependencies = (
\t\t\t);
\t\t\tname = {name};
\t\t\tproductName = {name};
\t\t\tproductReference = {PRODUCT_REF} /* {name}.app */;
\t\t\tproductType = "com.apple.product-type.application";
\t\t}};
/* End PBXNativeTarget section */

/* Begin PBXProject section */
\t\t{PROJECT} /* Project object */ = {{
\t\t\tisa = PBXProject;
\t\t\tattributes = {{
\t\t\t\tBuildIndependentTargetsInParallel = 1;
\t\t\t\tLastSwiftUpdateCheck = 1500;
\t\t\t\tLastUpgradeCheck = 1500;
\t\t\t\tTargetAttributes = {{
\t\t\t\t\t{TARGET} = {{
\t\t\t\t\t\tCreatedOnToolsVersion = 15.0;
\t\t\t\t\t}};
\t\t\t\t}};
\t\t\t}};
\t\t\tbuildConfigurationList = {PROJ_CL} /* Build configuration list for PBXProject "{name}" */;
\t\t\tcompatibilityVersion = "Xcode 14.0";
\t\t\tdevelopmentRegion = en;
\t\t\thasScannedForEncodings = 0;
\t\t\tknownRegions = (
\t\t\t\ten,
\t\t\t\tBase,
\t\t\t);
\t\t\tmainGroup = {MAIN_GROUP};
\t\t\tproductRefGroup = {PRODUCTS} /* Products */;
\t\t\tprojectDirPath = "";
\t\t\tprojectRoot = "";
\t\t\ttargets = (
\t\t\t\t{TARGET} /* {name} */,
\t\t\t);
\t\t}};
/* End PBXProject section */

/* Begin PBXResourcesBuildPhase section */
\t\t{RES_PHASE} /* Resources */ = {{
\t\t\tisa = PBXResourcesBuildPhase;
\t\t\tbuildActionMask = 2147483647;
\t\t\tfiles = (
{res_list}
\t\t\t);
\t\t\trunOnlyForDeploymentPostprocessing = 0;
\t\t}};
/* End PBXResourcesBuildPhase section */

/* Begin PBXSourcesBuildPhase section */
\t\t{SRC_PHASE} /* Sources */ = {{
\t\t\tisa = PBXSourcesBuildPhase;
\t\t\tbuildActionMask = 2147483647;
\t\t\tfiles = (
{src_list}
\t\t\t);
\t\t\trunOnlyForDeploymentPostprocessing = 0;
\t\t}};
/* End PBXSourcesBuildPhase section */

/* Begin XCBuildConfiguration section */
{config(PROJ_DEBUG, "Debug", proj_debug)}
{config(PROJ_RELEASE, "Release", proj_release)}
{config(TGT_DEBUG, "Debug", tgt)}
{config(TGT_RELEASE, "Release", tgt)}
/* End XCBuildConfiguration section */

/* Begin XCConfigurationList section */
\t\t{PROJ_CL} /* Build configuration list for PBXProject "{name}" */ = {{
\t\t\tisa = XCConfigurationList;
\t\t\tbuildConfigurations = (
\t\t\t\t{PROJ_DEBUG} /* Debug */,
\t\t\t\t{PROJ_RELEASE} /* Release */,
\t\t\t);
\t\t\tdefaultConfigurationIsVisible = 0;
\t\t\tdefaultConfigurationName = Release;
\t\t}};
\t\t{TGT_CL} /* Build configuration list for PBXNativeTarget "{name}" */ = {{
\t\t\tisa = XCConfigurationList;
\t\t\tbuildConfigurations = (
\t\t\t\t{TGT_DEBUG} /* Debug */,
\t\t\t\t{TGT_RELEASE} /* Release */,
\t\t\t);
\t\t\tdefaultConfigurationIsVisible = 0;
\t\t\tdefaultConfigurationName = Release;
\t\t}};
/* End XCConfigurationList section */
\t}};
\trootObject = {PROJECT} /* Project object */;
}}
"""
proj_dir = os.path.join(root, f"{name}.xcodeproj")
os.makedirs(proj_dir, exist_ok=True)
open(os.path.join(proj_dir, "project.pbxproj"), "w").write(out)

# Shared scheme so the project runs straight away.
scheme_dir = os.path.join(proj_dir, "xcshareddata", "xcschemes")
os.makedirs(scheme_dir, exist_ok=True)
ref = f"""<BuildableReference
               BuildableIdentifier = "primary"
               BlueprintIdentifier = "{TARGET}"
               BuildableName = "{name}.app"
               BlueprintName = "{name}"
               ReferencedContainer = "container:{name}.xcodeproj">
            </BuildableReference>"""
scheme = f"""<?xml version="1.0" encoding="UTF-8"?>
<Scheme LastUpgradeVersion = "1500" version = "1.7">
   <BuildAction parallelizeBuildables = "YES" buildImplicitDependencies = "YES">
      <BuildActionEntries>
         <BuildActionEntry buildForTesting = "YES" buildForRunning = "YES" buildForProfiling = "YES" buildForArchiving = "YES" buildForAnalyzing = "YES">
            {ref}
         </BuildActionEntry>
      </BuildActionEntries>
   </BuildAction>
   <TestAction buildConfiguration = "Debug" selectedDebuggerIdentifier = "Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier = "Xcode.DebuggerFoundation.Launcher.LLDB" shouldUseLaunchSchemeArgsEnv = "YES">
   </TestAction>
   <LaunchAction buildConfiguration = "Debug" selectedDebuggerIdentifier = "Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier = "Xcode.DebuggerFoundation.Launcher.LLDB" launchStyle = "0" useCustomWorkingDirectory = "NO" ignoresPersistentStateOnLaunch = "NO" debugDocumentVersioning = "YES" debugServiceExtension = "internal" allowLocationSimulation = "YES">
      <BuildableProductRunnable runnableDebuggingMode = "0">
         {ref}
      </BuildableProductRunnable>
   </LaunchAction>
   <ProfileAction buildConfiguration = "Release" shouldUseLaunchSchemeArgsEnv = "YES" savedToolIdentifier = "" useCustomWorkingDirectory = "NO" debugDocumentVersioning = "YES">
      <BuildableProductRunnable runnableDebuggingMode = "0">
         {ref}
      </BuildableProductRunnable>
   </ProfileAction>
   <AnalyzeAction buildConfiguration = "Debug">
   </AnalyzeAction>
   <ArchiveAction buildConfiguration = "Release" revealArchiveInOrganizer = "YES">
   </ArchiveAction>
</Scheme>
"""
open(os.path.join(scheme_dir, f"{name}.xcscheme"), "w").write(scheme)
print(f"{len(sources)} sources, {len(file_refs)} file refs")
