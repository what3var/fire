# ember on the command line

`ember` is the package manager of fire: it finds, installs, updates and removes packages, and builds and publishes your own. What packages are, the
sources, the lock file and packages straight from GitHub are described in [Packages and ember](../packages/README.md); this page is the command line.

There are two programs with the same commands, the same messages and exit codes, the same store and the same lock file - they can be swapped:

* the C# program `src/fire.Ember` (with the library `src/fire.Package.Manager`, which spark uses too) - built next to forge, as until now;
* **ember in fire**, `tools/ember/` - a fire project, the first larger tool of fire written in fire (docs/ROADMAP_C_PORT.md, Phase F). The C# program
  stays until the fire one has proven itself.

## Commands

| Command | What it does |
| --- | --- |
| `ember find [Name]` | searches the package sources (a part of a name or of a description; no name: everything); exit code 1 when nothing is found |
| `ember install Name[@Version]` | installs a package with what it depends on; also `ember install path/to/package.fpk` |
| `ember install github:owner/repo[@ref] [--name N] [--force]` | straight from a GitHub repository: the newest release, a tag, a branch or a commit (built from the source) |
| `ember update [Name \| github:owner/repo[@ref]]` | moves installed packages to their newest version (pinned tags and commits stay unless named) |
| `ember remove Name` | removes a package (also `github:owner/repo`); not one that another installed package needs |
| `ember list` | the installed packages (and where those from GitHub came from) |
| `ember sources` | the package sources in use, the GitHub API and the packages from GitHub with how they were checked |
| `ember create File.json` / `ember blank File.json` | a package description with example values (and the example files) / with all fields empty |
| `ember forge File.json [-o Dir]` | builds the package (`.fpk`) from the description; the description with absolute paths is kept in `Dir/json/` |
| `ember index Folder [BaseUrl]` | writes `Folder/index.json` for the `.fpk` files of the folder |
| `ember cache [clean]` | the user-wide cache of the static libraries of native builds: where it is and how big; `clean` empties it |

Exit codes: 0 done, 1 an error of the package manager (`ember: <message>` on the error output), 2 a wrong command line (the usage on the error output;
`ember` without arguments prints the usage and ends with 2, `ember help` with 0).

Both programs expect to live in the folder of the compiler: the store `Packages/`, `ember.json`, `PackageSource/`, `forge` and `Source/` are found next to
the program. Except for `create`, `blank`, `forge`, `index`, `cache` and `sources` every command first asks forge next to it to install the standard
packages that are missing or changed (`forge install-standard`).

## ember in fire

```
forge build tools/ember/ember.fireproj -o ember                    a VM program (the VM with the program behind it)
forge build tools/ember/ember.fireproj --engine native -o ember    a native one
```

Put the program next to forge (in place of the C# `ember`, or under another name). The files of `tools/ember/`:

| File | |
| --- | --- |
| `json.fire` | JSON as the C# ember reads and writes it, with `JsonNode` of the package `json`: comments and trailing commas when reading (`JsonReadOptions(true)`), the indented format and the escapers of System.Text.Json when writing; the typed reads of System.Text.Json (its messages) |
| `manifest.fire` | the package description (`package.json`), its check, the build order of the natives; versions |
| `fpk.fire` | the package file: reading, unpacking, `ember forge` |
| `store.fire` | the installed packages and `ember-lock.json` |
| `sources.fire` | folders, indexes, `ember.json`, downloads with size and SHA-256 (streamed into the file: `Http.Client.Send`), `ember index` |
| `github.fire` | `github:` specs, the REST API of GitHub, checksums of releases, the source snapshot (`Tar.Archive` of the package `compression`) |
| `service.fire` | install, update, remove, find |
| `standard.fire` | what ember asks forge for, the cache of native libraries, the templates of `create`/`blank` |
| `main.fire` | the command line |

**Compared** with `tests/compare-ember.sh <folder of the C# ember> <ember in fire>`: both run the same 45 commands (usage errors, sources, find, install
from a folder, a local web index and a fake GitHub on this machine - `tests/ember-fake-github.py` -, dependencies, update, remove, the lock file, a source
build, rate limit and 404 of GitHub, create, blank, forge, index, cache) in a store of their own at the same place; the output, the error output, the exit
codes and the files are compared. Only the bytes of a package one of them packs itself differ (another deflater), so the script compares such a package
by its entries and its `package.json`.
