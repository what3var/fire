# An agent: `#import "ai.agent"`

`AI.Agent` puts a small language model of the package [`ai`](README.md) to work: instructions, a conversation it remembers, tools it may call, and
choices among options. It is the core of the assistant of spark ([assistant.md](assistant.md)) and of any program that wants one.

```fire
#import "ai.agent"
#import "json"

var agent = new AI.Agent("LFM3-1.7B-QAD-Q4_0.gguf", "You help with the files of a project.")
agent.Tool("read_file", "Reads a text file of the project", ["path: the path of the file"], (JsonObject args) => File.ReadAllText(args.Get("path")))
agent.Tool("get_time", "Returns the current time", [], (JsonObject args) => "" + DateTime.Now)

print(agent.Ask("What is in notes.txt?"))                   // calls read_file, then answers from its content
print(agent.Ask("Who do I have to call, according to it?")) // the conversation goes on
print(agent.Choose("The user wrote: 'it crashes when I press Save'. What is it?", ["A bug report", "A question", "A feature request"]))   // 0
```

## Members

| | |
| --- | --- |
| `new AI.Agent(path, instructions = "", context = 4096)` | loads the model file; `new AI.Agent(model, instructions, context)` uses a model that is loaded (several agents can share one) |
| `Instructions`, `Settings`, `MaxSteps`, `Thinking` | the system text; how the answers are written (`AI.Settings`, temperature 0.3 by default); the rounds of tool calls of one question (6); let the model think first (Qwen 3.5, LFM 3: better for hard questions, slower - the thinking is not shown) |
| `Tool(name, description, parameters, handler)` | a tool with text parameters, each `"name: what it is"` (all required); the handler gets them as a `JsonObject` and returns its answer as text |
| `ToolWithSchema(name, description, schemaJson, handler)` | the parameters as a JSON schema (numbers, choices, optional ones - the tools of an MCP server) |
| `RemoveTool(name)`, `ToolCount`, `SystemPrompt` | |
| `Ask(text)` | the answer (waits); the model may call tools first |
| `Start(text)`, `Update()`, `Running`, `Cancel()` | the same for a user interface: `Update()` in its timer gives the new text of the answer, runs the tools when the model asks for them and goes on |
| `OnToolCall` | `(string name, JsonObject arguments) => ...` before a tool runs (to show "reading notes.txt ...") |
| `Calls` | the calls of the last question (`AI.AgentCall`: `Name`, `Arguments`, `Result`) |
| `Choose(question, options)` | the index of the most likely option: the model reads the question with the options and weighs each (it does not write); the conversation is not changed |
| `Once(question)` | an answer outside the conversation (nothing remembered, no tools) |
| `HistoryCount`, `HistoryRole(i)`, `HistoryText(i)` | the conversation as messages - kept, but shown only by those who want to |
| `Forget()` | a new topic: the conversation is forgotten; instructions and tools stay |
| `Close()` | frees the session (and the model when the agent loaded it) |

The conversation is kept as the model has read it, so the cache of the session holds it: the next question only computes what is new. When it
grows beyond the context, the oldest turns go.

## Tools and models

The tools are given to the model in its own format (Qwen 3.5: a `# Tools` section with `<tools>` JSON; LFM 3: `<tools>` after the instructions), and
the calls it writes are read in both forms the models use - XML (`<tool_call><function=name><parameter=p>value</parameter></function></tool_call>`) and
JSON (`<tool_call>{"name": ..., "arguments": {...}}</tool_call>`). A tool that throws gives the model `Error: ...` as its answer.

**LFM3-1.7B** is trained for tools: it calls them when they help and uses what they return. **Qwen3.5-0.8B** calls simple ones (the time) and
answers well, but often answers without a tool where one was needed - take it for answers, explanations and choices, LFM 3 for an agent with tools.
