These optional root-level scripts provide a Linux build/package workflow alongside
the existing project scripts. They do not deploy into the active game profile.

Build:

    bash build.sh

Output:

    Package/plugins/ValheimTomrer.dll

Package (automatically rebuilds Release):

    bash package.sh

Output:

    dist/ValheimTomrer-0.2.0.zip

Both commands detect Steam installations, additional Steam libraries, and BepInEx
in the game install or Gale/r2modman profiles. If there are several profiles,
select the intended one with BEPINEX_PATH.

Explicit paths:

    VALHEIM_INSTALL="/path/to/Valheim" BEPINEX_PATH="/path/to/profile/BepInEx" bash package.sh

Advanced: VALHEIM_MANAGED can point directly to the game's Managed directory.
TOMRER_SOURCE can point to the source folder if the scripts are stored elsewhere.
Scripts also work at the top of the earlier bundle containing source/ValheimTomrerMod.

build.sh defaults to Debug, for development. CONFIGURATION=Release bash build.sh
builds Release instead. Neither command installs or overwrites your active mod.
Close Valheim and replace your installed Tomrer DLL manually, keeping only one copy.
The package ZIP contains manifest.json, README.md, LICENSE, icon.png and
plugins/ValheimTomrer.dll (plus CHANGELOG.md when supplied).

The current fork release version is 0.2.0. To change it, update VERSION,
the csproj Version and src/Plugin.cs PluginVersion together; package.sh writes
the chosen version into its staged manifest without editing the upstream manifest.

Required: .NET SDK, Python 3, Valheim managed assemblies, BepInEx core assemblies.
No zip utility or Node.js is required.

Checked here: bash syntax and simulated build/package workflows, including
path detection, spaces in paths, Release packaging, and failed-build handling.
Actual .NET compilation and Valheim runtime testing remain unrun because
this environment does not have the required game assemblies or a .NET SDK.
