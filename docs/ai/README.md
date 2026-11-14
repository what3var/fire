# AI: a small language model (`#import "ai"`)

The package `ai` runs a small language model on the CPU - no service, no library, no GPU. Its C reads a model file in the **GGUF** format (the
format of llama.cpp) and works on a thread of its own, so a program with a user interface never waits for it.

```fire
#import "ai"

var model = new AI.Model("qwen2.5-0.5b-instruct-q4_0.gguf")
var session = new AI.Session(model)
print(session.Ask("You are a helpful assistant.", "What is the capital of France?"))   // The capital of France is Paris.
```

The assistant of spark ([assistant.md](assistant.md)) is built on it.

## Models

| | |
| --- | --- |
| Architectures | `qwen2` (Qwen 2 and 2.5, also the Coder models) and `llama` (Llama 2/3, SmolLM, TinyLlama) |
| Weights | `Q8_0`, `Q4_0`, `F16`, `F32` - take a `q8_0` or `q4_0` file (the K-quants such as `Q4_K_M` are not read: the model says so) |
| Tokenizer | the one in the file: byte-level BPE (Qwen, Llama 3, SmolLM) or SentencePiece (Llama 2) |

The default of the assistant is **Qwen2.5-0.5B-Instruct** (Apache 2.0): `qwen2.5-0.5b-instruct-q4_0.gguf` (430 MB) or `-q8_0.gguf` (680 MB),
from huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF. On four cores it reads a prompt at about 80 tokens a second and writes 25-30 tokens a second.
A model of this size answers simple questions, rewrites and explains short texts and picks from a few options well; it is no expert - it gets facts
and arithmetic wrong (it calls 8 a prime number), so a program uses it for help and suggestions, not for answers that must be right.

The products use AVX2 (x86-64, chosen when the processor has it) or NEON (ARM64); elsewhere plain C. `model.Kernels` says which.

## Sessions

A `Session` holds the **cache** of what the model has read (`context` tokens: prompt and answer together, 2048 by default; about 12 KB per token for
the 0.5B model). A prompt that starts like the one before only computes the new part: the same instructions before every question, or a chat that
grows, cost only what is new.

| Member | |
| --- | --- |
| `new AI.Session(model, context = 2048, threads = 0)` | `threads` 0: as many as the machine has (8 at most) |
| `Start(prompt, settings)` | starts writing the text after `prompt`; returns at once |
| `Take()` | the text written since the last call (whole characters only) |
| `State`, `Busy` | `Idle`, `Prompt` (reading the prompt), `Generating`, `Done`, `Failed` (`Error`), `Cancelled` |
| `Cancel()` | stops the job after the token it is working on |
| `StartChoice(prompt, options, perToken)`, `Choice`, `Score(i)` | weighs the options as continuations of the prompt: `Choice` is the most likely, `Score` its log-probability |
| `Complete(prompt, settings)`, `Ask(system, question, settings)`, `Choose(prompt, options)` | the same, waiting for the result |
| `PromptTokens`, `PromptDone`, `Generated`, `PromptMilliseconds`, `GenerateMilliseconds` | for a progress bar and statistics |
| `Forget()` | empties the cache |

A user interface starts a job and takes the text in its timer or frame loop:

```fire
session.Start(AI.Chat.Question("", question))
// ... in the timer of the window:
if (session.Busy || session.State == AI.State.Done) { bubble.Text += session.Take() }
```

## Prompts

The models are trained on **ChatML**: `AI.Chat` writes it.

```fire
var chat = new AI.Chat()
chat.System("You answer in one sentence.")
chat.User("What does a compiler do?")
var answer = session.Complete(chat.Prompt())
chat.Assistant(answer)
chat.User("And an interpreter?")
print(session.Complete(chat.Prompt()))      // only the new part is read
```

`AI.Chat.Question(system, question, answerStart)` is the prompt of one question; `answerStart` puts words into the model's mouth (`"Sure, here"`).

## Settings

| Setting | Default | |
| --- | --- | --- |
| `Temperature` | 0.7 | 0: always the most likely token - the same answer every time (`AI.Settings.Exact()`) |
| `TopK`, `TopP`, `MinP` | 40, 0.95, 0.05 | which tokens are possible at all |
| `RepeatPenalty` | 1.1 | makes tokens of the last 64 less likely |
| `MaxTokens` | 512 | 0: until the model ends its answer or the context is full |
| `Seed` | 0 | 0: random; another number: the same answer for the same prompt |
| `Stops` | empty | texts that end the answer (they are not part of it) |

## Multiple choice

`Choose` does not let the model write: it computes how likely each option is as the continuation of the prompt. That is fast (no sampling, the options
share the prompt) and the answer is always one of the options:

```fire
var prompt = AI.Chat.Question("", "The user wrote: 'it crashes when I click Save'. What kind of message is this?")
var kind = session.Choose(prompt, ["A bug report", "A question", "A feature request"])
```

## Errors

`AI.AIException`: the file is missing, not a GGUF model, of an architecture or with weights that are not supported, cut off; the prompt does not fit
into the context; out of memory. A running job that fails sets `State.Failed` and `Error`.

The model file is read with the rights of the program (the policy of the host applies, as for every file); it is mapped into memory, not copied.
