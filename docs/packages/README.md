# Packages and `ember`

| Page | |
| --- | --- |
| **This page** | using packages: `ember`, sources, the standard libraries, the cache |
| [Building a package](building.md) | a package in fire only - from a library project or from a description |
| [Packages with natives](natives.md) | C or C++ code in a package: how to write it and connect it to fire |
| [forge](forge.md) and [script](script.md) | the compiler in your program, and fire scripts run in your program |

fire can import more than the libraries that come with the compiler (`graphics`, `io`, `time`, ...): a **package** brings one or more imports of its own, for example the
interface to a sensor, a protocol or an operating system API. `#import "name"` finds the imports of the installed packages like the built-in ones.

`ember` is the package manager (a fire program, `tools/ember`; [its command line](../tools/ember.md)). The editor has a small window for it
(*Tools > Package Manager (ember)...*) that runs it.

## Using packages

```
ember find sensor          search the package sources (a part of a name or description; no name: everything)
ember install sensorkit    install the newest version (and what it depends on); sensorkit@1.2.0 installs that version
ember install kit.fpk      install a package file
ember list                 the installed packages
ember remove sensorkit     remove a package (not one that another installed package needs)
```

Packages are installed **for the machine**: below `Packages\<name>\` in the folder of the compiler (the folder of `ember` itself). Every script sees the same packages.
A script uses an import of a package like any other:

```
#import "sensorkit"
print(SensorKit.Read(3))
```

An import that is not installed is an error that points to `ember`. The live diagnostics of the editor know the installed imports; after the Package Manager window
installed or removed something, the open scripts are checked again.

### Sources

`ember find` and `ember install` look in the *package sources*. `ember.json` next to the compiler lists them; without the file these are
the folder `PackageSource` next to the compiler and the index of the package registry on the website, https://what3var.github.io/packages/
(`ember sources` shows the ones in use):

```json
{ "sources": [ "PackageSource", "https://what3var.github.io/packages/index.json", "D:\\my-packages\\index.json" ] }
```

A source is a **folder** (`.fpk` files, and/or an `index.json`) or an **index** (an address of the web, a `file:` address or the path of a json file). The index:

```json
{
  "format": 1,
  "packages": [
    { "name": "sensorkit", "author": "Jane", "description": "Sensors on the I2C bus", "license": "MIT", "homepage": "https://...",
      "versions": [ { "version": "1.0.0", "url": "files/sensorkit/sensorkit-1.0.0.fpk", "sha256": "...", "size": 4711 },
                    { "version": "1.1.0", "url": "...", "sha256": "...", "size": 4802, "yanked": true } ] }
  ]
}
```

The `url` is relative to the index or absolute. After the download ember checks `size` and `sha256`; a file that differs is not installed. Other fields are
ignored (the index of the website has more: the date, the dependencies and imports of each version, the page of the package); an index of a newer `format` is refused.
A version that is **yanked** (withdrawn, e.g. a broken release) is never taken as the newest, but `ember install name@1.1.0` still installs it (with a note).

From the web ember reads only **https** (plain http only from this machine - `localhost`, a test server), and it installs a package from the web only when the index
gives its `sha256`. A source that cannot be read (no network) is reported and skipped.

`ember index Folder [BaseUrl]` writes the `index.json` for the `.fpk` files of a folder (with licence, size and checksum), ready to be put on a web page together with them.

### Straight from GitHub

A package can be installed from a GitHub repository without an entry in any index:

```
ember install github:jane/sensorkit            the newest release
ember install github:jane/sensorkit@v1.2.0     that release (a tag; @1.2.0 finds v1.2.0 too, and the other way round)
ember install github:jane/sensorkit@main       a branch: built from the source of its newest commit
ember install github:jane/sensorkit@3f2c1a0    a commit (7 to 40 hex digits): built from its source
ember install github:jane/fire-sensors --name sensorkit    (or github:jane/fire-sensors#sensorkit) a package not named like the repository
ember update                                    move the packages to their newest version (below)
ember remove github:jane/sensorkit              (or by its name)
```

The same spec works wherever a package is named: in the `dependencies` of a `package.json` (`"dependencies": [ "github:jane/sensorkit@v1.2.0" ]`) and as a package
reference of a project (`{ "package": "github:jane/sensorkit@v1.2.0" }`); such a dependency counts as there when the lock file (below) says a package came from
that repository. `ember list` shows where each package from GitHub came from (`github:jane/sensorkit@v1.2.0 (commit 3f2c1a0)`), `ember sources` the API in use, the
token's origin and the packages from GitHub with how they were checked. In spark, type `github:owner/repo[@ref]` into the search field of the Package Manager: the row
it shows installs that.

**How it resolves.** ember asks the REST API of GitHub: `GET /repos/{owner}/{repo}/releases/latest` (no ref) or `/releases/tags/{tag}`, and
`/commits/{ref}` for the commit of the tag, branch or commit. A release that has a `.fpk` asset installs that file; with several `.fpk` assets the one named
like the package (`sensorkit.fpk`, `sensorkit-1.2.0.fpk`) is taken, else it is an error that asks for `#name`. Without a release of that name, or a release without
a `.fpk`, ember gets the source of the commit (`GET /repos/{owner}/{repo}/tarball/{commit}`) and builds the package from it: a `package.json` in the root of the
repository is packed like `ember forge` packs it, else the one library project (`.fireproj`) of the root (or the only one of the tree) is packed like *Pack Library*
(ember asks `forge build` next to it; spark and forge do it themselves). A repository without releases needs a ref (`@main`). Drafts and pre-releases are not the
newest release (that is what `releases/latest` gives); a release that was deleted is simply not found any more - the GitHub way of yanking.

**How it is checked.** The asset is checked against its `digest` (`sha256:...`, which the API gives for assets), an asset `<name>.fpk.sha256` and/or a `SHA256SUMS`
(or `SHA256SUMS.txt`) asset with a line for it; checksums that disagree are an error. A release with none of these is **trusted on first use**: ember says clearly that
the file was *not verified upstream*, records its SHA-256 in the lock file and refuses another file under the same tag from then on. A source build is pinned by its
commit. Like everything from the web: https only (http only from this machine), at most 64 MB for a package and its `size` must match what the API says, at most
100 MB for a source snapshot (400 MB, 20000 files unpacked; links are not unpacked, no path may leave the folder). A `package.json` or project in the source that
names a file outside the repository (an absolute path, `..` out of it) is refused - the build reads only the repository's files. The package must be named like the
repository (case does not matter) unless `#name`/`--name` accepts its name; names starting with `fire-` and `standard` imports are refused; a package of the same
name that came from elsewhere is replaced only with `--force`. The install prints the repository it comes from.

**The lock file.** `ember-lock.json` in the folder of the installed packages (`Packages/`, next to the package folders) records every package from GitHub:

```json
{ "format": 1, "github": [ { "repository": "jane/sensorkit", "ref": "", "kind": "release", "tag": "v1.2.0", "commit": "3f2c1a0...", "asset": "sensorkit-1.2.0.fpk",
    "sha256": "...", "verified": "asset digest", "package": "sensorkit", "version": "1.2.0", "installed": "2026-11-18T10:00:00Z" } ] }
```

`kind` is `release` or `source`, `verified` one of `asset digest`, `sha256 asset`, `SHA256SUMS` (joined with `and` when several agree), `first use` or `source`. Installing
the same repository again takes what the lock file pins - the same tag (also for "the newest release") or, for a branch, the same commit - and the file must have the
recorded checksum; a tag that now points to another commit or an asset that was replaced is an error (remove the package and install it again only if you trust the
new one). `ember update` resolves anew: a package installed as the newest release moves to the newest release, one from a branch to the branch's newest commit;
one pinned to a tag or a commit stays, unless the update names it (`ember update github:jane/sensorkit` moves it to the newest release, `ember update
github:jane/sensorkit@v2.0.0` to that tag). Packages from the index sources move to their newest version that is not yanked. `ember remove` drops the entry.

**The API and the token.** `FIRE_GITHUB_API` (default `https://api.github.com`; for tests or GitHub Enterprise) and a token from `FIRE_GITHUB_TOKEN`, `GITHUB_TOKEN` or
ember.json (`"github": { "api": "...", "token": "..." }`; the environment first). The token is sent only to the host and port of the API: redirects are followed one
by one, and a hop to another host (the storage of release assets, `codeload.github.com`) goes without it. With a token an asset is fetched through the API (works for
private repositories), without one from its `browser_download_url`. Without a token GitHub allows 60 requests an hour; when the limit is used up (403/429 with no
requests left, or `Retry-After`) ember says so with the time it resets.

### Publishing

The packages of the registry are published by the maintainer of the website (repository `what3var.github.io`, folder `registry/`, see its `README.md`): there are no
accounts and no uploads. To have a package listed, publish its source and the `.fpk` (for example as a release of your repository) and open an issue with the
link, the licence and the SHA-256 of the file. Without a listing, such a release can be installed straight away with `ember install github:owner/repo` ("Straight from GitHub" above) - attach the `.fpk` (GitHub gives its digest) and, if you like, a `SHA256SUMS`.

Build the package with `ember forge` (or *Pack Library* for a library project). The **documents** of the package go into its root: `README.md` (shown on the page
of the package), `LICENSE`/`LICENCE`/`COPYING` and `NOTICE` (most licences want their text with every copy) - the files of these names next to the description (or
in the folder of the library project), or the ones `"documents": [ ... ]` names. A package needs a `license` (an SPDX id such as `MIT` or `Apache-2.0`) that allows
passing it on; package names starting with `fire-` and the import names of fire are reserved.

## The standard libraries (`packages/`, `Source/`)

The libraries of fire - `graphics`, `windows`, `devices`, `io`, `filesystem`, `dialogs`, `ui`, `ui.docking`, `linq`, `reflection`, `time`, `random`, `json`, `circuits`, `circuits.ui`, `testing`, `net`, `tls`, `http`, `gpio`, `i2c`, `spi`, `wifi`, `audio`,
`tracker`, `scene3d`, `scene3d.terrain`, `scene3d.effects`, `scene3d.physics` - are ordinary **library projects** ([Projects and solutions](../tools/projects.md)) that you can open, change and test like your own: the folder `packages/` of the repository holds the solution
`Packages.firesln` with one project per import:

```
packages/
  Packages.firesln
  time/
    time.fireproj        "name": "fire-time", "import": "time", "standard": true, the natives (sources, functions, reset, link libraries)
    time.fire            the fire code (the prelude of the package)
    native/              the C++ (fire_bridge_time.hpp): the natives, for the VM (a library) and for native builds
    testing/             the tests of the library (forge test), not part of the package
  ui/
    00_core.fire ...     a library may have several files; `#import "graphics"` in them makes graphics a requirement of the package
  ...
```

The build copies the folder next to the programs as **`Source/`** (`src/StandardSources.targets`, imported by the compiler, the editor and the tests) and builds the libraries of the C++ for this
machine into `Prebuilt/` (`forge prebuild-standard Source Prebuilt`). When **spark** or **forge** start (ember asks forge: `forge install-standard`), every project of `Source/Packages.firesln` that
is not installed yet, or whose folder changed since it was installed, is packed (without its tests) and installed - the ones it imports first. A fingerprint of every project folder in
`Packages/.source-stamps.json` keeps the check quick. A prebuilt library goes along when it was built from the same C++, so that the virtual machine needs no C++ compiler for the libraries
as shipped; after a change of the C++ the library is built on first use like that of any package. The VM also takes a library from `Prebuilt/` directly when the one next to the installed
package is missing or outdated (a package that did not change is not installed again after a new build of fire). The library of `graphics` and `windows` for the VM (`fire_gfx`) is
prebuilt the same way (`Prebuilt/fire-gfx/`).

A library is built against the runtime (`fire_rt.h`, the ABI, the platform package) and a generated wrapper: a hash of both is kept next to it (`<library>.runtime`). Only when that
changes is the library built again - not with every new build of fire whose runtime stayed the same (before, the time stamp of the fire.Native assembly decided, and every change of
the generator needed a C compiler on the next start).

**Changing a standard library**: *Tools > Open the Standard Libraries (Source)* opens the solution in spark. Change a file, run its tests (`forge test`, or the tests of the project), then
*Install* the project (context menu of the project, or *Project > Install Library*): it is packed and installed at once; an installed package of the same version is replaced after a question.
The next start of spark or forge would install it, too.

What makes these projects special is small: `"standard": true` lets a project have the import name of a library of the compiler (`time`, `graphics`, ...), `"serializable": false` (in `ui`)
keeps its classes out of JSON ([JSON](../stdlib/json.md); written to the import in `package.json`), and `"native": { "discover": false,
"functions": [...] }` names the natives explicitly (`"c": "fire_io_FileOpen"` for a package in C, `"cpp": "mathkit::Add"` for one in C++). Five imports are known to the compiler by name: `graphics`,
`windows`, `ui`, `linq`, `reflection` - their fire code still comes from the package; the C of `graphics` and `windows` runs in the VM (as a library built for
the machine) and in native builds alike. Their natives are declared in their projects as well, under `"native": { "bridge": [...] }` (the same entries as
`functions`: `name`, `arguments`, `c`, `needsList`): they are not a library of the package of their own but live together in the one library of the display
(its windows and framebuffers share one state), and the compiler calls them directly - a new native of a window is one line there. Only the core prelude (`Exception`,
`List<T>`, `Math`, string methods, ...) is built into the compiler.

Inside its own project a library is the program: `#import "time"` in the tests of `time` means the project's files (and its C++, built for the VM), not the installed package.

### Device drivers

A library with drivers of the device framework names them in its project file, `"drivers": ["MyDriver", "Kit.OtherDriver"]` (classes made with `new Class()`).
`forge` writes them to the import in `package.json` (`"drivers": [...]`). spark loads the drivers of the installed packages and shows their devices in its device
manager ([Devices](../devices/README.md)).

### Plugins

A package (or a library of a project) can be the interface between a program and its plugins ([libraries and plugins](../libraries/README.md)): the program and the plugin both import it, the plugin implements its
interfaces or derives from its classes, the program creates the plugin's classes by name after `Library.Load` ([Native code](../language/18-native-code.md), [Natives Backend (AOT): fire -> C](../spec/NATIVE_BACKEND.md)
"Plugins"). The plugin loads only into a program whose package has the same interface (the hash of its classes and members) - a new version of the package that only
changes the code of its methods keeps it.

### The cache of native libraries

A native build with `"linkage": "static"` ([Natives Backend (AOT): fire -> C](../spec/NATIVE_BACKEND.md) "Getrennte Übersetzung") turns every package (and the prelude, every library of a project) into a static
library (`"shared"`: a shared library, on Linux and macOS, a DLL on Windows with MinGW) and keeps it in a cache of the user: `~/.local/share/fire/cache/native` on Linux, `%LOCALAPPDATA%\fire\cache\native` on Windows, `~/Library/Application Support/fire/cache/native`
on macOS (`FIRE_NATIVE_CACHE` moves it). An entry is found by the content hash of everything its C depends on, so a stale entry is never used, only left behind.

```
ember cache          where the cache is and how big it is
ember cache clean    empty it
```

## Where it is in the code

* `tools/ember/` (fire): the manifest, the package file, the installed packages, the sources, install/remove/update, packages straight from GitHub and the lock
  file ([ember on the command line](../tools/ember.md)). What forge needs of packages to build (the manifest, the store, `.fpk`) is C in `native/forge/project`;
  what spark reads (the manifest, the natives, the store, the manifest of an `.fpk`) is `src/fire.Project/PackageModel`. The C# ember (`src/fire.Ember`,
  `src/fire.Package.Manager`) is gone with milestone M3 of [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md).
* The compiler keeps the imports of packages in its set of imports under the key `pkg:name` (`PackageStore.KeyPrefix`): `ImportedPreludes` resolves `#import`, inserts the prelude
  and registers the names of the natives; `LinkedProgram.PackageNatives` carries those names so that a run in the virtual machine (also of a packed program) keeps the
  indexes of the native calls right.
* The native generator (`CppGenerator.CollectPackages`) checks the platforms, puts the C++ sources of the target into the generated file and calls the functions.
* For the virtual machine `PackageLibrary` (fire.Compiler) builds the shared library of an import (generated wrapper + `native/abi/` + the runtime in library mode, `FIRE_LIBRARY`) or
  finds the prebuilt one; `fire`, the VM in C, loads it and marshals the values (`native/vm/vm_pkg.c`). `LinkedProgram.PackageNativeLibraries` names the library of each native; a packed program carries
  the libraries in its payload (`CVm.Pack`).
