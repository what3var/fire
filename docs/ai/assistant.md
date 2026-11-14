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

The assistant is started by spark (stdio: spark starts the program and talks over its standard input and output) or connects to a running spark
(HTTP on localhost, with a token spark writes into the user's settings folder). Its window has no frame and stays next to spark's window.

## Principles

* **Off by default.** The setup has its own, unchecked task "The assistant (downloads a model of about 500 MB when it is first switched on)". In spark
  it can be switched on and off at any time (*Tools > Assistant*), and "Don't show again" in every bubble switches it off.
* **The model only after asking.** The first time it is switched on, the assistant says what it will download, from where and how large it is, and waits
  for "Download". Without a model it still gives the tips that need none (the rules below).
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

* a text (Markdown: code in `backticks` and blocks) that grows while the model writes;
* up to four **choices** as buttons ("Show me how", "Explain the error", "Not now") - multiple choice answers come from `Session.Choose` or from
  the rule that made the tip;
* a line to type a question (Enter asks, Esc closes);
* a small menu: Show history, New topic, Characters, Don't show tips like this, Switch off.

## What it knows

* **Rules without a model**: tips at the right moment ("You can run the program with F5", "This error: a `take` is missing - see Ownership"), from
  the error codes (docs/tools/errors.md), the file kinds and the first steps of the help.
* **The context** spark hands in: the kind of file, the word at the caret, the selected text, the last error and build result. The prompt gets only
  what the question needs.
* **The documentation**: the pages of the help as small pieces; the piece that fits the question best (words in common) goes into the prompt, so
  the small model answers about fire and not about a language it guesses.

## Later

* **Code completion**: the Coder models (`qwen2.5-coder-0.5b`) write the rest of a line or a small function (fill in the middle: `<|fim_prefix|>`,
  `<|fim_suffix|>`, `<|fim_middle|>`), shown grey after the caret.
* **Small tasks**: "write a function that ...", "explain this function", "make this a loop" - the answer as a change the user can take or leave.
* A larger model (Qwen2.5-1.5B) for those who want it, chosen in the settings.

## Steps

| Step | | State |
| --- | --- | --- |
| 1 | The package `ai`: GGUF; qwen2, qwen3, qwen35 (gated delta net), lfm3 (short convolutions, window attention), llama; Q8_0/Q4_0/Q4_K/Q5_K/Q6_K/F16/F32, the tokenizers, the cache and checkpoints, sampling, multiple choice, threads, AVX2/NEON | built |
| 1b | The package `ai.agent`: an agent with instructions, tools (lambdas or the tools of an MCP server), the loop of tool calls, choices | |
| 2 | The package `mcp`: JSON-RPC 2.0, client and server, stdio and HTTP (Streamable HTTP) | |
| 3 | The package `assistant`: characters, bubble, choices, the question line, rules, the model download | |
| 4 | spark as an MCP server (tools, resources, notifications), *Tools > Assistant*, the setup task | |
| 5 | Code completion and small tasks | |
