# The assistant of spark

A small helper at the edge of the editor, in the spirit of the office assistants of the nineties - but **off unless the user wants it**, quiet, and
backed by a small language model that runs on the user's own computer ([the package `ai`](README.md)). Nothing leaves the machine.

It is written in fire and talks to the editor over the **Model Context Protocol** (MCP): spark is an MCP server, the assistant an MCP client. So the
assistant does not depend on how spark is built - a spark written in fire offers the same server, and any other MCP client (an agent, another tool)
can use spark the same way.

```
 spark (MCP server)                                   assistant (fire program, its own window)
   tools:     editor_state, selection, diagnostics,  <-- JSON-RPC 2.0 -->   package `mcp` (client)
              open_file, insert_text, build, run                             package `ai` (the model)
   resources: the files of the solution, the help                            package `ui` (character, bubble)
   notifications: build finished, program failed, file changed
```

spark starts the assistant (`forge run Assistant/assistant.fire`, the package `assistant`) when it is switched on, and the assistant connects to spark's
MCP server (HTTP on localhost, with the token of the connection file, [spark as an MCP server](../tools/spark-mcp.md)). It ends with spark. Its first start
builds the packages it uses for this machine (a minute or two); spark's status bar shows that with a small bar. Its window is
small, without a frame, above the other windows and transparent where the desktop allows it: only the character and its bubble are seen, at the
bottom right of the desktop. Dragging the character moves it; a click pokes it.

## Principles

* **Off by default.** The setup has its own, unchecked task "The assistant" (spark takes it over once, at its first start). In spark it can be switched
  on and off at any time (*Tools > Assistant*).
* **Poked, not pushing.** At its very first start it says "Just poke me when you have a question: a click on me, or Ctrl+^ in spark." Afterwards it
  starts quietly (only the character, asleep). A poke - a click on it, or **Ctrl+^** in spark (the key left of 1) - opens the bubble with the question
  line; a second poke closes it.
* **The model only after asking.** The first poke without a model offers the one that fits the machine ([the models](models.md): Qwen3.5 0.8B, 507 MB;
  on a good machine also LFM3 1.7B, with its license shown first) and waits for "Download"; the download shows its progress, can stop and go on, and
  is checked by its SHA-256. Without a model it still gives the tips that need none (the rules below).
* **Quiet.** It does not interrupt typing. It speaks when it is asked, when something went wrong (a build failed, the program threw) or at a pause -
  and a tip it gave is not given again.
* **No history on screen.** A question gets an answer in the bubble; the chat behind it is kept (the cache makes follow-up questions cheap) but not
  shown. "Show history" shows it, "New topic" forgets it.

## The model

Qwen3.5-0.8B (Apache 2.0, 507 MB) by default; LFM3-1.7B (1.05 GB, LFM Open License: free below 10 million USD revenue a year) as the stronger choice in
the settings - it is trained for tools, the right one when the assistant acts in spark through MCP.

## The characters

Three own characters, drawn with `graphics` (vector shapes, so they scale): **Sparky** (a small flame, the default - lively), **Flint** (a stone with
a spark, calm and short in its answers) and **Wick** (a little candle, friendly and patient). Each has the same animations - idle (breathing,
blinking), appear and disappear, think (while the model works), talk (while the answer comes), point (at a place of the editor), wave, sleep - and
its own way of speaking (a line in the system prompt). They are not, and do not look like, the assistants of other products.

## The bubble

* small buttons at its top right: the history, a new topic, another character, close;
* as high as what it shows - from a small bubble up to Min(screen height - 120, 480) pixels; beyond that the text scrolls (and fades at the bottom edge). It grows
  upwards: the character stays where it is. Closed, the window is only the character;
* a text that grows while the model writes - as high as the bubble when there are no choices and no question line;
* up to four **choices** as buttons ("Download", "Explain it", "Not now"); a long one breaks into lines;
* after a poke, the question line ("Ask me..."; Enter asks).

## What it knows

* **Rules without a model**: tips at the right moment ("You can run the program with F5", "This error: a `take` is missing - see Ownership"), from
  the error codes (docs/tools/errors.md), the file kinds and the first steps of the help.
* **The context** spark hands in: the kind of file, the word at the caret, the selected text, the last error and build result. The prompt gets only
  what the question needs.
* **The documentation**: search_docs and read_doc - spark's tools when it is connected, else its own (the package's `Assistant.Docs`: the pages of
  `Help/Docs` beside spark, `FIRE_DOCS`, or the `docs` of a checkout; loaded in about a second, a search takes some 60 ms in the VM). The model looks up what it needs (a few
  sections at a time), so it answers about fire and not about a language it guesses.

## Later

* **Code completion**: the Coder models (`qwen2.5-coder-0.5b`) write the rest of a line or a small function (fill in the middle: `<|fim_prefix|>`,
  `<|fim_suffix|>`, `<|fim_middle|>`), shown grey after the caret.
* **Small tasks**: "write a function that ...", "explain this function", "make this a loop" - the answer as a change the user can take or leave.
* A larger model (Qwen2.5-1.5B) for those who want it, chosen in the settings.

## Steps

| Step | | State |
| --- | --- | --- |
| 1 | The package `ai`: GGUF; qwen2, qwen3, qwen35 (gated delta net), lfm3 (short convolutions, window attention), llama; Q8_0/Q4_0/Q4_K/Q5_K/Q6_K/F16/F32, the tokenizers, the cache and checkpoints, sampling, multiple choice, threads, AVX2/NEON | built |
| 1b | The package `ai.agent`: an agent with instructions, tools (lambdas or the tools of an MCP server), the loop of tool calls, choices | built |
| 2 | The package `mcp`: JSON-RPC 2.0, client and server, stdio and HTTP (Streamable HTTP) | built |
| 2b | The package `ai.models`: the catalog, the choice by the machine, the download (resumable, SHA-256) | built |
| 3 | The package `assistant`: characters, bubble, choices, the question line, tips, the model setup, build errors explained | built |
| 4 | spark as an MCP server (tools, resources, notifications), *Tools > Assistant*, Ctrl+^, the setup task | built |
| 4b | A window without a frame that stays on top, at the bottom right of the desktop; dragging the character moves it; no button in the task bar, the character as its picture (`Window.Frameless`, `TopMost`, `SetPosition`, `WorkArea`, `BeginMove`, `ShowInTaskbar`, `SetIcon`) | built |
| 5 | Code completion and small tasks | |
