# The models (package `ai.models`)

`#import "ai.models"` knows the small language models that suit [the package `ai`](README.md): which there are, which one fits this machine, where they
are, and how they come there - a download that can stop and go on, checked by its SHA-256.

```fire
#import "ai.models"

AI.ModelInfo m = AI.Models.Recommended()          // the model for this machine (no question asked)
if (!m.Installed()) {
    print("Download " + m.Name + " (" + m.SizeText() + ", " + m.License + ")?")   // ask first
    var setup = new AI.ModelInstaller(m)
    while (setup.Step()) { print(setup.Percent() + "%") }
    if (!setup.Ok()) { print(setup.Error) }
}
var model = new AI.Model(m.Path())
```

## The catalog

| Id | | Size | License | Memory (needs / pleasant) |
| --- | --- | --- | --- | --- |
| `qwen3.5-0.8b` | Qwen3.5 0.8B, the default: tips, choices, short answers | 507 MB | Apache 2.0 | 2 / 4 GB |
| `lfm3-1.7b` | LFM3 1.7B: stronger with tools and longer answers, about half as fast | 1.05 GB | LFM Open License v1.0 (free below 10 million USD revenue a year) | 4 / 8 GB |
| `qwen3.5-2b` | Qwen3.5 2B: better answers, slower | 1.21 GB | Apache 2.0 | 6 / 12 GB |
| `qwen2.5-0.5b` | Qwen2.5 0.5B: the smallest, for weak machines | 429 MB | Apache 2.0 | 1 / 2 GB |

`AI.Models.Catalog()` is this list (`AI.ModelInfo`: `Id`, `Name`, `Description`, `Repo`, `File`, `Size`, `Sha256`, `License`, `LicenseUrl`,
`MinMemoryMB`, `GoodMemoryMB`; `Url()`, `Path()`, `Installed()`, `SizeText()`), `AI.Models.Find(id or file name)` one of it.

## The choice

`AI.Machine` (package `ai`) tells the threads (`Cores()`), the memory (`MemoryMB()`, `FreeMemoryMB()`) and whether the fast products run (`Simd()`:
AVX2 or NEON). From it:

* `AI.Models.Recommended()` - Qwen3.5 0.8B; Qwen2.5 0.5B on a weak machine (less than 3 GB memory, or no SIMD and less than 4 threads).
* `AI.Models.Stronger()` - LFM3 1.7B when the machine is good for it (8 GB, 4 threads, SIMD), else `undefined`: a program offers it, it does not take it
  by itself (its license is not Apache).
* `AI.Models.Fits(m)` - whether a model can run here at all.
* `AI.Models.Installed()` - the models that are there; `AI.Models.Pick(preferred)` - the path of the preferred one when it is there, else of the first
  one that is there ("" when there is none); `AI.Models.Remove(m)`.

## The folder

`AI.Models.Folder()`: the variable `FIRE_MODELS` when it is set, else `fire/models` in the user's application data - `%AppData%\fire\models` on Windows,
`~/.config/fire/models` on Linux, `~/Library/Application Support/fire/models` on macOS. A file of the right name and size there counts as installed (so a
model downloaded by hand can be put there).

The files come from Hugging Face (`https://huggingface.co/<repo>/resolve/main/<file>`), or from a mirror with the same paths: `AI.Models.Mirror = "..."`
or the variable `FIRE_MODELS_MIRROR`. A proxy is taken from `HTTPS_PROXY` ([http, "Proxies"](../networking/http.md)).

## The installer

`new AI.ModelInstaller(m)` does the work in steps, so a user interface calls `Step()` in its timer (`Run()` waits until it is done):

1. the download into `<file>.part` (a stopped download goes on where it stopped; after an error of the network it tries three times more);
2. the check: the size, then the SHA-256 of the catalog - a wrong file is removed;
3. `Done()` and `Ok()`; `Error` says what went wrong.

`State` (`AI.InstallState`: `Waiting`, `Downloading`, `Checking`, `Done`, `Failed`), `Received()`, `Total()` and `Percent()` are for a progress bar;
`Close()` stops it (what came stays). A model that is there already is only checked.
