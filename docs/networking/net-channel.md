# Net.Channel: a channel over TCP

`#import "net.channel"` gives `Net.Channel<T>`: the [Channel<T>](../language/15-threads-and-actors.md#channels) of a program over a TCP connection.
Every side has an end of its own: `Send` puts a value into the other side's queue, `Receive` takes the next one out of its own. It behaves like a
channel between threads - the same words, the same lending, the same end.

```fire
#import "net.channel"
record Note { string text; construct(string t) { this.text = t } }

var listener = new Net.ChannelListener<any>("127.0.0.1", 0)   // port 0: any free port
int port = listener.Port
fire taking port {
    var ch = new Net.Channel<any>("127.0.0.1", port)          // connects (the limit: 10 s unless given)
    any note = ch.Receive()
    ch.Send(note.text.Length)
    ch.Close()
}
var ch = listener.Accept()
var note = new Note("hello")
ch.Send(note)                       // lent: `note` stays this program's
print(ch.Receive())                 // 5
print(ch.Receive() == undefined)    // True - the other side closed
ch.Close()                          // (a channel of the globals: the program ends once its threads do)
```

- A value (a number, a text, a bool) goes as it is, as JSON (`Json.Serialize`, read back with its `$type`).
- An object or an array goes only fresh (`new`, the result of a call) or with a word - `Send(copy x)`, `Send(take x)` - and then belongs to the other
  side alone: the sender's references are dead from then on (Send destroys what it was given). Without a word it is the ArgumentException. It comes
  back when the other side sends it back - as a new value for `Receive`.
- A record without a word is **lent**: the sender stays its owner and keeps using it; the other side gets it to read (fields, methods, `==`,
  `copy r`), `take r` there is the AccessDeniedException. When the owner destroys it or moves it on, the lending ends: the other side's next use is the
  DestroyedException. The end travels on the connection like any message - what the sender sends after it arrives after it.
- `Receive(limit)` waits at most `limit` (the TimeoutException), `TryReceive()` does not wait, `foreach` receives until the other side closed.
- `Close()` ends this side's direction: the other side receives what was sent, then undefined. When a `Net.Channel` is destroyed it closes, the
  rest is sent, and the connection ends - with it the records the other side had lent to this one. A channel in a global variable is closed by hand:
  its threads run until it is (the program waits for its threads before it destroys its globals).
- When the connection breaks, the receiving side's queue closes and the records lent to it end.
- `Net.ChannelListener<T>(host, port)` waits for channels: `Accept(timeout)` gives the next one, `Port` the port, `Close()` stops listening.

On the wire every message is one line of UTF-8: `v <json>` (a value that moved), `l <id> <json>` (a lent record), `g <id>` (its lending ended), `c`
(the sender closed its direction). Each end has two threads: one sends what `Send` queued, one reads and puts what arrives into the queue - it owns
the records lent to this side.
