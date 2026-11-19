# AI: a small language model (`#import "ai"`)

The package `ai` runs a small language model on the CPU - no service, no library, no GPU. Its C reads a model file in the **GGUF** format (the
format of llama.cpp) and works on a thread of its own, so a program with a user interface never waits for it.

```fire
#import "ai"

var model = new AI.Model("Qwen3.5-0.8B-Q4_0.gguf")
var session = new AI.Session(model)
print(session.Ask("You are a helpful assistant.", "What is the capital of France?"))   // The capital of France is Paris.
```

The assistant of spark ([assistant.md](assistant.md)) is built on it.

## Models

| | |
| --- | --- |
| Architectures | `qwen35` (Qwen 3.5: three of four layers a gated delta net, every fourth gated attention), `lfm3` (LFM 3: short convolutions, attention over all positions and over a window), `qwen3`, `qwen2` (Qwen 2.5, also the Coder models), `llama` (Llama, SmolLM, TinyLlama) |
| Weights | `Q8_0`, `Q4_0`, `F16`, `F32`, and in the mixed files `Q4_1`, `Q4_K`, `Q5_K`, `Q6_K` (converted to Q8_0 when the model is loaded): the `Q8_0`, `Q4_0` and `Q4_K_M` files of the model repositories |
| Tokenizer | the one in the file: byte-level BPE (Qwen, Llama 3, LFM, SmolLM) or SentencePiece (Llama 2) |

| Model | File | Size | License | |
| --- | --- | --- | --- | --- |
| **Qwen3.5-0.8B** (the default of the assistant) | unsloth/Qwen3.5-0.8B-GGUF, `Qwen3.5-0.8B-Q4_0.gguf` | 507 MB | Apache 2.0 | fast, good German and code, picks well from options |
| **LFM3-1.7B** (the "stronger" choice) | LiquidAI/LFM3-1.7B-GGUF, `LFM3-1.7B-QAD-Q4_0.gguf` | 1.05 GB | LFM Open License 1.0 (free, also commercially, below 10 million USD revenue a year) | trained for tools and agents; the best answers here |
| Qwen2.5-0.5B-Instruct | Qwen/Qwen2.5-0.5B-Instruct-GGUF, `qwen2.5-0.5b-instruct-q4_0.gguf` | 430 MB | Apache 2.0 | the smallest, the weakest |

On four cores (AVX2) a prompt is read at 25-35 tokens a second per thread group (Qwen3.5-0.8B: 30 ms a token, LFM3-1.7B: 45 ms) and the answer is
written at 16 (Qwen3.5-0.8B) or 11 (LFM3-1.7B) tokens a second. Models of this size explain, rewrite, summarize and pick from a few options well;
they are no experts - they get facts and arithmetic wrong (the 0.8B model calls 7 no prime number), so a program uses them for help and suggestions,
not for answers that must be right.

The products use AVX2 (x86-64, chosen when the processor has it) or NEON (ARM64); elsewhere plain C. `model.Kernels` says which. The engine was
checked against llama.cpp (the same tokens and the five most likely next tokens for Qwen 2.5, 3 and 3.5; LFM 3 by its perplexity on English text).

## Sessions

A `Session` holds the **cache** of what the model has read (`context` tokens: prompt and answer together, 2048 by default). A prompt that starts like
the one before only computes the new part: the same instructions before every question, or a chat that grows, cost only what is new. Models with
recurrent layers (Qwen 3.5, LFM 3) keep a state instead of the keys of every position - small, and the same for any length; they come back to the end
of the last prompt (a checkpoint), so the options of a choice and the next question of a chat are cheap there, too. `Keep(start)` names the start
every prompt to come begins with (the instructions and tools of an agent): its state is kept as well, so a prompt whose later part changed (a chat
that left out its oldest turns) does not compute the instructions again.

| Member | |
| --- | --- |
| `new AI.Session(model, context = 2048, threads = 0)` | `threads` 0: one fewer than the machine has (8 at most); they run below the normal priority (the program's own thread - its window - and the other programs go first; the C define `FIRE_AI_NORMAL_PRIORITY` keeps the normal one) |
| `Start(prompt, settings)` | starts writing the text after `prompt`; returns at once |
| `Take()` | the text written since the last call (whole characters only) |
| `State`, `Busy` | `Idle`, `Prompt` (reading the prompt), `Generating`, `Done`, `Failed` (`Error`), `Cancelled` |
| `Cancel()` | stops the job after the token it is working on |
| `StartChoice(prompt, options, perToken)`, `Choice`, `Score(i)` | weighs the options as continuations of the prompt: `Choice` is the most likely, `Score` its log-probability |
| `Complete(prompt, settings)`, `Ask(system, question, settings)`, `Choose(prompt, options)` | the same, waiting for the result |
| `PromptTokens`, `PromptDone`, `PromptReused`, `Generated`, `PromptMilliseconds`, `GenerateMilliseconds`, `TokensPerSecond` | for a progress bar and statistics - also while the job runs (`PromptDone` grows while the prompt is read; `PromptReused`: the tokens that came from the cache) |
| `EndReason` | why the text ended: `AI.End.Model` (the model ended it), `MaxTokens`, `Context` (the context was full), `StopText`, `Cancelled`, `Failed` |
| `Threads` | the threads that compute |
| `Keep(start)` | every prompt to come begins with `start`: its state stays computed (see above) |
| `Forget()` | empties the cache |

A user interface starts a job and takes the text in its timer or frame loop:

```fire
session.Start(AI.Chat.Question("", question))
// ... in the timer of the window:
if (session.Busy || session.State == AI.State.Done) { bubble.Text += session.Take() }
```

## Prompts

The models are trained on **ChatML**: `AI.Chat` writes it; `For(model)` adds what a model wants besides (the start token of LFM, the empty thinking
block that makes Qwen 3 / 3.5 and LFM 3 answer at once; `Thinking = true` lets them think first, `AI.Chat.WithoutThinking(text)` removes it again).

```fire
var chat = new AI.Chat()
chat.For(model)
chat.System("You answer in one sentence.")
chat.User("What does a compiler do?")
var answer = session.Complete(chat.Prompt())
chat.Assistant(answer)
chat.User("And an interpreter?")
print(session.Complete(chat.Prompt()))      // only the new part is read
```

`AI.Chat.Question(system, question, answerStart, model)` is the prompt of one question; `answerStart` puts words into the model's mouth (`"Sure, here"`).
`session.Ask(system, question)` uses the format of its model.

## Settings

| Setting | Default | |
| --- | --- | --- |
| `Temperature` | 0.7 | 0: always the most likely token - the same answer every time (`AI.Settings.Exact()`) |
| `TopK`, `TopP`, `MinP` | 40, 0.95, 0.05 | which tokens are possible at all |
| `RepeatPenalty` | 1.1 | makes tokens of the last 64 less likely |
| `MaxTokens` | 512 | 0: until the model ends its answer or the context is full (`EndReason` says which) |
| `Seed` | 0 | 0: random; another number: the same answer for the same prompt |
| `Stops` | empty | texts that end the answer (they are not part of it) |

## Multiple choice

`Choose` does not let the model write: it computes how likely each option is as the continuation of the prompt. That is fast (no sampling, the options
share the prompt) and the answer is always one of the options. It works best when the question lists the options and the answer is begun:

```fire
var q = "The user wrote: 'it crashes when I click Save'. What kind of message is this?\n\nOptions:\n- A bug report\n- A question\n- A feature request"
var prompt = AI.Chat.Question("You answer correctly and briefly.", q, "Answer:", model)
var kind = session.Choose(prompt, [" A bug report", " A question", " A feature request"], true)
```

(The agent of the package `ai.agent` does this for you: `agent.Choose(question, options)`.)

## Errors

`AI.AIException`: the file is missing, not a GGUF model, of an architecture or with weights that are not supported, cut off; the prompt does not fit
into the context; out of memory. A running job that fails sets `State.Failed` and `Error`.

The model file is read with the rights of the program (the policy of the host applies, as for every file); it is mapped into memory, not copied.
