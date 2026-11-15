# spark as an MCP server

spark can offer its editor to AI tools on the same computer over the **Model Context Protocol** (MCP, [the package `mcp`](../networking/mcp.md)): the
documents, the errors, the output, the commands Check, Run and Stop - and the documentation and the samples of fire. The assistant of spark
([docs/ai/assistant.md](../ai/assistant.md)) works this way, and so can any other MCP client.

It is **off** until it is switched on: *Tools > MCP Server (for AI Tools)* (or by switching on the assistant).

## Connecting

The server listens on `127.0.0.1` only (a free port each time spark starts) and speaks Streamable HTTP (versions 2025-11-25 down to 2024-11-05). Every
request needs a token. spark writes both into a file only the user can read, and removes it when it closes:

| | |
| --- | --- |
| Windows | `%AppData%\fire\spark-mcp.json` |
| Linux | `~/.config/fire/spark-mcp.json` |
| macOS | `~/Library/Application Support/fire/spark-mcp.json` (the ApplicationData folder of .NET) |

```json
{"url": "http://127.0.0.1:40155/mcp", "token": "abcd...", "pid": 8405, "server": "spark"}
```

A client sends `Authorization: Bearer <token>`; a request without it is answered with 401, one from a web page (an `Origin` that is not localhost) with
403. From fire:

```fire
#import "mcp"
#import "json"
#import "filesystem"

any conn = Json.Parse(IO.File.ReadAllText("/home/me/.config/fire/spark-mcp.json"))   // (the file of the table above)
var transport = new Mcp.HttpTransport(conn.Get("url"))
transport.Headers.Set("Authorization", "Bearer " + conn.Get("token"))
var spark = new Mcp.Client(transport)
spark.Initialize("my-tool")
print(spark.CallTool("get_diagnostics").Text)
```

## Tools

| Tool | |
| --- | --- |
| `spark_state` | the solution, the active document (path, caret line and column, selection), the open documents, whether a program runs or a build goes on, the number of errors and warnings (JSON) |
| `read_document` | the text of a document - the open one as it is in the editor, else the file; `firstLine` / `lastLine`; each line with its number |
| `get_selection` | the selected text with its file and lines |
| `get_diagnostics` | the error list: one line per error or warning (severity, code, file:line, message) |
| `open_file` | opens a file (or shows it) at a line |
| `replace_selection` | replaces the selection (or puts text in at the caret) |
| `replace_lines` | replaces lines of a document (opens it when it is not); `lastLine = firstLine - 1` inserts |
| `list_files` | the projects of the solution and their files |
| `check` | compiles like Start, without running; the result and the errors (no dialog) |
| `run`, `stop`, `get_output` | starts the program (F5), stops it, what it wrote |
| `search_docs` | searches the documentation of fire (the language, the libraries, the tools): three sections with file and heading |
| `read_doc` | one section of a page (without a heading: the headings of the page) |
| `list_samples`, `list_sample_files`, `read_sample_file` | the sample programs, the files of one, one file |

Every change goes through the editor: the user sees it and can undo it (Ctrl+Z); nothing is saved by a tool. The tools give back little at a time (a few
hits, one section, one file) - small models have a small context.

Resources: `spark://state` (the state as JSON; subscribe to it - it changes with the active document, a build and a run) and `spark://output`.
Notifications (`notifications/message`, logger `spark`): `{"event": "build", "ok": ..., "errors": ..., "warnings": ...}`, `run started`, `run ended` - on the
stream of the session (GET), or with the next answer.

## Other clients

Any MCP client that speaks Streamable HTTP with a header can connect, e.g. an agent on the command line:

```
claude mcp add --transport http spark http://127.0.0.1:40155/mcp --header "Authorization: Bearer <token>"
```

(The port and the token change when spark starts again: take them from the connection file.)
