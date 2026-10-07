namespace fire.Standard
{
    /// <summary>
    /// `#import "http"`: an HTTP/1.1 client and a small HTTP server (docs/NETWORK.md), written in fire on the `net` package (a connection is an `IO.Stream`, so nothing here is
    /// specific to TCP: the same code runs over TLS). Everything is in `namespace Http`.
    ///
    ///   var client = new Http.Client()
    ///   var r = client.Get("http://example.org/")
    ///   print(r.status + " " + r.Text())
    ///
    ///   var server = new Http.Server("127.0.0.1", 8080)
    ///   server.Route("GET", "/hello", func (req) => Http.Response.FromText("hello " + req.Query("name", "world")))
    ///   server.Run()
    ///
    /// The client follows redirects, understands `Content-Length`, chunked bodies and bodies that end with the connection, and sends `Connection: close`. The server handles one connection after
    /// the other (`ServeOne`, `Run`) and answers `Connection: close`: simple and light enough for a microcontroller; run several servers in several fire threads if you need more.
    /// Errors are `Http.HttpException` (a `code`), network errors are the `Net.NetException`s of the net package.
    /// </summary>
    public static class HttpPrelude
    {
        public const string Source = """
            namespace Http {
                // 1 bad URL, 2 bad message (a malformed request or response), 3 too many redirects, 4 too large, 5 not supported
                class HttpException : Exception {
                    string message
                    int code

                    construct(string message, int code = 2) {
                        this.message = message
                        this.code = code
                    }
                }

                // Numbers in text.
                class Num {
                    // A whole number in decimal digits, -1 if there are none or something else is in the text.
                    static int Parse(string text) {
                        var t = text.Trim()
                        if (t.Length == 0 || t.Length > 15) { return -1 }
                        var n = 0
                        for (var i = 0; i < t.Length; i++) {
                            var d = t.CharAt(i).ToInt() - 48
                            if (d < 0 || d > 9) { return -1 }
                            n = n * 10 + d
                        }
                        return n
                    }

                    // The value of a hexadecimal digit (character code), -1 if it is none.
                    static int HexDigit(int c) {
                        if (c >= 48 && c <= 57) { return c - 48 }
                        if (c >= 97 && c <= 102) { return c - 87 }
                        if (c >= 65 && c <= 70) { return c - 55 }
                        return -1
                    }

                    // A number in hexadecimal digits (the size of a chunk; a ";extension" after it is ignored), -1 if it is not one.
                    static int ParseHex(string text) {
                        var t = text.Trim()
                        var semi = t.IndexOf(";")
                        if (semi >= 0) { t = t.Substring(0, semi).Trim() }
                        if (t.Length == 0 || t.Length > 8) { return -1 }
                        var n = 0
                        for (var i = 0; i < t.Length; i++) {
                            var d = Http.Num.HexDigit(t.CharAt(i).ToInt())
                            if (d < 0) { return -1 }
                            n = n * 16 + d
                        }
                        return n
                    }
                }

                // Percent encoding of URLs (UTF-8).
                class Uri {
                    static string Digit(int d) {
                        if (d < 10) { return "" + d }
                        if (d == 10) { return "A" }
                        if (d == 11) { return "B" }
                        if (d == 12) { return "C" }
                        if (d == 13) { return "D" }
                        if (d == 14) { return "E" }
                        return "F"
                    }

                    // The text for use in a URL: letters, digits and - _ . ~ stay, everything else becomes %XX (a space becomes %20).
                    static string Encode(string text) {
                        var bytes = IO.Utf8.GetBytes(text)
                        var result = ""
                        for (var i = 0; i < bytes.length; i++) {
                            var b = bytes[i]
                            var keep = (b >= 48 && b <= 57) || (b >= 65 && b <= 90) || (b >= 97 && b <= 122) || b == 45 || b == 95 || b == 46 || b == 126
                            if (keep) { result = result + b.ToChar() }
                            else { result = result + "%" + Http.Uri.Digit(b / 16) + Http.Uri.Digit(b % 16) }
                        }
                        return result
                    }

                    // The text of a percent encoded one (`+` is a space with `plus = true`, as in a query); a malformed %XX stays as it is.
                    static string Decode(string text, bool plus = false) {
                        var sink = new IO.MemoryStream()
                        var i = 0
                        while (i < text.Length) {
                            var c = text.CharAt(i).ToInt()
                            var hi = -1
                            var lo = -1
                            if (c == 37 && i + 2 < text.Length) {
                                hi = Http.Num.HexDigit(text.CharAt(i + 1).ToInt())
                                lo = Http.Num.HexDigit(text.CharAt(i + 2).ToInt())
                            }
                            if (hi >= 0 && lo >= 0) {
                                sink.WriteByte(hi * 16 + lo)
                                i = i + 3
                            } else if (c == 43 && plus) {
                                sink.WriteByte(32)
                                i = i + 1
                            } else {
                                var one = IO.Utf8.GetBytes(text.Substring(i, 1))
                                sink.Write(one, 0, one.length)
                                i = i + 1
                            }
                        }
                        var bytes = sink.ToBuffer()
                        sink.Close()
                        return IO.Utf8.GetString(bytes, 0, bytes.length)
                    }
                }

                // scheme://host:port/path?query
                class Url {
                    string scheme
                    string host
                    int port
                    string path

                    construct(string scheme, string host, int port, string path) {
                        this.scheme = scheme
                        this.host = host
                        this.port = port
                        this.path = path
                    }

                    // Reads an absolute http:// or https:// URL; Http.HttpException (code 1) for anything else.
                    static Parse(string text) {
                        var t = text.Trim()
                        var sep = t.IndexOf("://")
                        if (sep <= 0) { throw new Http.HttpException("Not an absolute URL: '" + text + "'.", 1) }
                        var scheme = t.Substring(0, sep).ToLower()
                        if (scheme != "http" && scheme != "https") { throw new Http.HttpException("Unsupported URL scheme '" + scheme + "'.", 1) }
                        var rest = t.Substring(sep + 3)
                        var hash = rest.IndexOf("#")
                        if (hash >= 0) { rest = rest.Substring(0, hash) }
                        var slash = rest.IndexOf("/")
                        var question = rest.IndexOf("?")
                        if (question >= 0 && (slash < 0 || question < slash)) { slash = question }
                        var authority = rest
                        var path = "/"
                        if (slash >= 0) {
                            authority = rest.Substring(0, slash)
                            path = rest.Substring(slash)
                            if (path.StartsWith("?")) { path = "/" + path }
                        }
                        if (authority.IndexOf("@") >= 0) { authority = authority.Substring(authority.IndexOf("@") + 1) }
                        var port = 80
                        if (scheme == "https") { port = 443 }
                        var host = authority
                        if (authority.StartsWith("[")) {
                            var close = authority.IndexOf("]")
                            if (close < 0) { throw new Http.HttpException("Bad host in '" + text + "'.", 1) }
                            host = authority.Substring(1, close - 1)
                            var after = authority.Substring(close + 1)
                            if (after.StartsWith(":")) { port = Http.Num.Parse(after.Substring(1)) }
                        } else {
                            var colon = authority.LastIndexOf(":")
                            if (colon >= 0) {
                                host = authority.Substring(0, colon)
                                port = Http.Num.Parse(authority.Substring(colon + 1))
                            }
                        }
                        if (host.Length == 0 || port < 1 || port > 65535) { throw new Http.HttpException("Bad host or port in '" + text + "'.", 1) }
                        return new Http.Url(scheme, host, port, path)
                    }

                    // The value of the Host header.
                    string HostHeader() {
                        var h = this.host
                        if (h.IndexOf(":") >= 0) { h = "[" + h + "]" }
                        var standard = 80
                        if (this.scheme == "https") { standard = 443 }
                        if (this.port != standard) { h = h + ":" + this.port }
                        return h
                    }

                    // The URL a `Location` header points to, seen from this one (absolute, `//host/...`, `/path` or relative).
                    string Resolve(string location) {
                        if (location.StartsWith("http://") || location.StartsWith("https://")) { return location }
                        var origin = this.scheme + "://" + this.HostHeader()
                        if (location.StartsWith("//")) { return this.scheme + ":" + location }
                        if (location.StartsWith("/")) { return origin + location }
                        var dir = this.path
                        var q = dir.IndexOf("?")
                        if (q >= 0) { dir = dir.Substring(0, q) }
                        var last = dir.LastIndexOf("/")
                        if (last >= 0) { dir = dir.Substring(0, last + 1) } else { dir = "/" }
                        return origin + dir + location
                    }

                    string ToString() { return this.scheme + "://" + this.HostHeader() + this.path }
                }

                // The header fields of a message: names are compared without regard to case, the order is kept.
                class Headers {
                    List names
                    List values

                    construct() {
                        this.names = new List()
                        this.values = new List()
                    }

                    int Count { get { return this.names.count } }

                    int IndexOf(string name) {
                        var lower = name.ToLower()
                        for (var i = 0; i < this.names.count; i++) {
                            if (this.names[i].ToLower() == lower) { return i }
                        }
                        return -1
                    }

                    bool Has(string name) { return this.IndexOf(name) >= 0 }

                    // The first value of the field, undefined if there is none.
                    Get(string name) {
                        var i = this.IndexOf(name)
                        if (i < 0) { return undefined }
                        return this.values[i]
                    }

                    // Sets the field (replaces the others of that name); returns the headers for chaining.
                    Set(string name, string value) {
                        this.Remove(name)
                        this.names.Add(name)
                        this.values.Add(value)
                        return this
                    }

                    // Adds a field (a name can appear several times).
                    Add(string name, string value) {
                        this.names.Add(name)
                        this.values.Add(value)
                        return this
                    }

                    Remove(string name) {
                        var lower = name.ToLower()
                        var i = this.names.count - 1
                        while (i >= 0) {
                            if (this.names[i].ToLower() == lower) {
                                this.names.RemoveAt(i)
                                this.values.RemoveAt(i)
                            }
                            i = i - 1
                        }
                    }

                    // Name and value of field i (0 .. Count - 1) as "Name: value".
                    string Line(int i) { return this.names[i] + ": " + this.values[i] }

                    // All fields as "Name: value\r\n" lines.
                    string ToString() {
                        var text = ""
                        for (var i = 0; i < this.names.count; i++) { text = text + this.names[i] + ": " + this.values[i] + "\r\n" }
                        return text
                    }
                }

                class Status {
                    // The standard reason phrase of a status code.
                    static string Reason(int code) {
                        if (code == 100) { return "Continue" }
                        if (code == 200) { return "OK" }
                        if (code == 201) { return "Created" }
                        if (code == 202) { return "Accepted" }
                        if (code == 204) { return "No Content" }
                        if (code == 206) { return "Partial Content" }
                        if (code == 301) { return "Moved Permanently" }
                        if (code == 302) { return "Found" }
                        if (code == 303) { return "See Other" }
                        if (code == 304) { return "Not Modified" }
                        if (code == 307) { return "Temporary Redirect" }
                        if (code == 308) { return "Permanent Redirect" }
                        if (code == 400) { return "Bad Request" }
                        if (code == 401) { return "Unauthorized" }
                        if (code == 403) { return "Forbidden" }
                        if (code == 404) { return "Not Found" }
                        if (code == 405) { return "Method Not Allowed" }
                        if (code == 408) { return "Request Timeout" }
                        if (code == 409) { return "Conflict" }
                        if (code == 411) { return "Length Required" }
                        if (code == 413) { return "Payload Too Large" }
                        if (code == 414) { return "URI Too Long" }
                        if (code == 415) { return "Unsupported Media Type" }
                        if (code == 431) { return "Request Header Fields Too Large" }
                        if (code == 500) { return "Internal Server Error" }
                        if (code == 501) { return "Not Implemented" }
                        if (code == 502) { return "Bad Gateway" }
                        if (code == 503) { return "Service Unavailable" }
                        if (code == 505) { return "HTTP Version Not Supported" }
                        return "Status " + code
                    }

                    // true for the codes whose message has no body (1xx, 204, 304).
                    static bool NoBody(int code) { return code < 200 || code == 204 || code == 304 }
                }

                // A response: what the client got, and what a route returns.
                class Response {
                    int status
                    string reason
                    Headers headers
                    var body
                    string url

                    construct(int status = 200, body = undefined) {
                        this.status = status
                        this.reason = Http.Status.Reason(status)
                        this.headers = new Http.Headers()
                        this.url = ""
                        this.body = Http.Response.Bytes(body)
                    }

                    // The body as bytes: a text becomes UTF-8, a buffer stays, undefined is empty.
                    static Bytes(body) {
                        if (body == undefined) { return new byte[0] }
                        if (body is of string) { return IO.Utf8.GetBytes(body) }
                        return body
                    }

                    // A text response (default `text/plain; charset=utf-8`).
                    static FromText(string text, int status = 200, string contentType = "text/plain; charset=utf-8") {
                        var r = new Http.Response(status, text)
                        r.headers.Set("Content-Type", contentType)
                        return r
                    }

                    static FromHtml(string html, int status = 200) { return Http.Response.FromText(html, status, "text/html; charset=utf-8") }

                    static FromJson(string json, int status = 200) { return Http.Response.FromText(json, status, "application/json") }

                    // A response with only a status line and an explanation as text.
                    static Error(int status, string message = undefined) {
                        if (message == undefined) { message = Http.Status.Reason(status) }
                        return Http.Response.FromText(message, status)
                    }

                    // 303/302 to another address.
                    static Redirect(string location, int status = 302) {
                        var r = new Http.Response(status)
                        r.headers.Set("Location", location)
                        return r
                    }

                    bool Ok { get { return this.status >= 200 && this.status < 300 } }

                    int Length { get { return this.body.length } }

                    // The body as text (UTF-8).
                    string Text() { return IO.Utf8.GetString(this.body, 0, this.body.length) }

                    // The value of a header field, undefined if it is not there.
                    Header(string name) { return this.headers.Get(name) }
                }

                // A request as the server gets it.
                class Request {
                    string method
                    string target
                    string path
                    string queryText
                    string version
                    Headers headers
                    var body
                    string remoteHost

                    construct(string method, string target) {
                        this.method = method
                        this.target = target
                        this.version = "HTTP/1.1"
                        this.headers = new Http.Headers()
                        this.body = new byte[0]
                        this.remoteHost = ""
                        var q = target.IndexOf("?")
                        if (q < 0) {
                            this.path = Http.Uri.Decode(target)
                            this.queryText = ""
                        } else {
                            this.path = Http.Uri.Decode(target.Substring(0, q))
                            this.queryText = target.Substring(q + 1)
                        }
                    }

                    // The value of a query parameter (`?name=value&...`, percent decoded), `fallback` if it is not there.
                    Query(string name, fallback = undefined) {
                        if (this.queryText.Length == 0) { return fallback }
                        var pairs = this.queryText.Split("&")
                        for (var i = 0; i < pairs.length; i++) {
                            var pair = pairs[i]
                            var eq = pair.IndexOf("=")
                            var key = pair
                            var value = ""
                            if (eq >= 0) {
                                key = pair.Substring(0, eq)
                                value = pair.Substring(eq + 1)
                            }
                            if (Http.Uri.Decode(key, true) == name) { return Http.Uri.Decode(value, true) }
                        }
                        return fallback
                    }

                    // The body as text (UTF-8).
                    string Text() { return IO.Utf8.GetString(this.body, 0, this.body.length) }

                    Header(string name) { return this.headers.Get(name) }
                }

                // Reads from a stream with a buffer of its own: lines, exact numbers of bytes, the rest.
                class Reader {
                    var source
                    var chunk
                    int pos
                    int len
                    bool eof

                    construct(source) {
                        this.source = source
                        this.chunk = new byte[4096]
                        this.pos = 0
                        this.len = 0
                        this.eof = false
                    }

                    // Makes sure chunk[pos..len) has data; false at the end of the stream.
                    bool Fill() {
                        if (this.pos < this.len) { return true }
                        if (this.eof) { return false }
                        var n = this.source.Read(this.chunk, 0, 4096)
                        if (n <= 0) {
                            this.eof = true
                            this.pos = 0
                            this.len = 0
                            return false
                        }
                        this.pos = 0
                        this.len = n
                        return true
                    }

                    // The next line without its line break, undefined at the end of the stream (a line longer than `limit` bytes is an error, code 4).
                    ReadLine(int limit = 8192) {
                        var pending = undefined
                        var total = 0
                        while (this.Fill()) {
                            var nl = __IOBufferIndexOf(this.chunk, this.pos, this.len - this.pos, 10)
                            var upto = this.len
                            if (nl >= 0) { upto = nl }
                            total = total + (upto - this.pos)
                            if (total > limit) { throw new Http.HttpException("A line is longer than " + limit + " bytes.", 4) }
                            if (nl >= 0) {
                                var text = undefined
                                if (pending == undefined) {
                                    var count = nl - this.pos
                                    if (count > 0 && this.chunk[nl - 1] == 13) { count = count - 1 }
                                    text = IO.Utf8.GetString(this.chunk, this.pos, count)
                                } else {
                                    pending.Write(this.chunk, this.pos, nl - this.pos)
                                    var bytes = pending.ToBuffer()
                                    var count2 = bytes.length
                                    if (count2 > 0 && bytes[count2 - 1] == 13) { count2 = count2 - 1 }
                                    text = IO.Utf8.GetString(bytes, 0, count2)
                                    pending.Close()
                                }
                                this.pos = nl + 1
                                return text
                            }
                            if (pending == undefined) { pending = new IO.MemoryStream() }
                            pending.Write(this.chunk, this.pos, this.len - this.pos)
                            this.pos = this.len
                        }
                        if (pending == undefined) { return undefined }
                        var rest = pending.ToBuffer()
                        pending.Close()
                        if (rest.length == 0) { return undefined }
                        return IO.Utf8.GetString(rest, 0, rest.length)
                    }

                    // Exactly `count` bytes; Http.HttpException (code 2) if the stream ends first.
                    ReadExact(int count) {
                        var result = new byte[count]
                        var done = 0
                        while (done < count && this.pos < this.len) {
                            result[done] = this.chunk[this.pos]
                            done = done + 1
                            this.pos = this.pos + 1
                        }
                        while (done < count) {
                            var n = this.source.Read(result, done, count - done)
                            if (n <= 0) { throw new Http.HttpException("The connection ended before the whole message arrived.", 2) }
                            done = done + n
                        }
                        return result
                    }

                    // Everything up to the end of the stream (an error of code 4 beyond `limit` bytes).
                    ReadToEnd(int limit) {
                        var sink = new IO.MemoryStream()
                        if (this.pos < this.len) {
                            sink.Write(this.chunk, this.pos, this.len - this.pos)
                            this.pos = this.len
                        }
                        var tmp = new byte[4096]
                        while (!this.eof) {
                            var n = this.source.Read(tmp, 0, 4096)
                            if (n <= 0) { this.eof = true }
                            else {
                                sink.Write(tmp, 0, n)
                                if (sink.Length > limit) { throw new Http.HttpException("The message is larger than " + limit + " bytes.", 4) }
                            }
                        }
                        var bytes = sink.ToBuffer()
                        sink.Close()
                        return bytes
                    }
                }

                // The message format on the wire.
                class Wire {
                    // The header fields up to the empty line.
                    static ReadHeaders(Reader reader, Headers headers) {
                        var count = 0
                        while (true) {
                            var line = reader.ReadLine(8192)
                            if (line == undefined) { throw new Http.HttpException("The connection ended inside the header.", 2) }
                            if (line.Length == 0) { return }
                            count = count + 1
                            if (count > 100) { throw new Http.HttpException("Too many header fields.", 4) }
                            var colon = line.IndexOf(":")
                            if (colon <= 0) { throw new Http.HttpException("A header field without a name: '" + line + "'.", 2) }
                            headers.Add(line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim())
                        }
                    }

                    // The body of a message: chunked, with a Content-Length, or (a response only: `untilClose`) up to the end of the connection.
                    static ReadBody(Reader reader, Headers headers, bool untilClose, int limit) {
                        var te = headers.Get("Transfer-Encoding")
                        if (te != undefined && te.ToLower().IndexOf("chunked") >= 0) {
                            var sink = new IO.MemoryStream()
                            while (true) {
                                var sizeLine = reader.ReadLine(8192)
                                if (sizeLine == undefined) { throw new Http.HttpException("The connection ended inside a chunked body.", 2) }
                                var size = Http.Num.ParseHex(sizeLine)
                                if (size < 0) { throw new Http.HttpException("A bad chunk size: '" + sizeLine + "'.", 2) }
                                if (size == 0) {
                                    // the trailer fields, then the empty line
                                    var trailer = reader.ReadLine(8192)
                                    while (trailer != undefined && trailer.Length > 0) { trailer = reader.ReadLine(8192) }
                                    break
                                }
                                if (sink.Length + size > limit) { throw new Http.HttpException("The message is larger than " + limit + " bytes.", 4) }
                                var data = reader.ReadExact(size)
                                sink.Write(data, 0, data.length)
                                reader.ReadLine(8192)
                            }
                            var all = sink.ToBuffer()
                            sink.Close()
                            return all
                        }
                        var cl = headers.Get("Content-Length")
                        if (cl != undefined) {
                            var n = Http.Num.Parse(cl)
                            if (n < 0) { throw new Http.HttpException("A bad Content-Length: '" + cl + "'.", 2) }
                            if (n > limit) { throw new Http.HttpException("The message is larger than " + limit + " bytes.", 4) }
                            return reader.ReadExact(n)
                        }
                        if (untilClose) { return reader.ReadToEnd(limit) }
                        return new byte[0]
                    }

                    // Writes the start line, the header fields, the empty line and the body.
                    static Write(stream, string startLine, Headers headers, body) {
                        var head = IO.Utf8.GetBytes(startLine + "\r\n" + headers.ToString() + "\r\n")
                        stream.Write(head, 0, head.length)
                        if (body != undefined && body.length > 0) { stream.Write(body, 0, body.length) }
                    }
                }

                // Opens the connection to a URL (the scheme decides: http is a TCP connection, https a TLS connection over it).
                class Transport {
                    static Open(Url url, int timeout, tls = undefined) {
                        if (url.scheme == "http") {
                            var tcp = new Net.TcpClient(url.host, url.port, timeout)
                            tcp.NoDelay = true
                            return tcp
                        }
                        if (url.scheme == "https") { return Tls.Stream.Connect(url.host, url.port, tls, timeout) }
                        throw new Http.HttpException("The scheme '" + url.scheme + "' is not supported.", 5)
                    }
                }

                class Client {
                    int timeout
                    int maxRedirects
                    bool followRedirects
                    int maxBodyBytes
                    string userAgent
                    Headers headers
                    // The Tls.Options for https:// URLs (undefined: verify against the system's certificates).
                    var tls

                    construct() {
                        this.tls = undefined
                        this.timeout = 30000
                        this.maxRedirects = 5
                        this.followRedirects = true
                        this.maxBodyBytes = 16777216
                        this.userAgent = "fire-http/1"
                        this.headers = new Http.Headers()
                    }

                    // GET, POST, PUT, DELETE, HEAD: `headers` is an Http.Headers (or undefined); a body is a text (UTF-8) or a byte buffer.
                    Get(string url, headers = undefined) { return this.Request("GET", url, undefined, headers) }
                    Head(string url, headers = undefined) { return this.Request("HEAD", url, undefined, headers) }
                    Delete(string url, headers = undefined) { return this.Request("DELETE", url, undefined, headers) }

                    Post(string url, body, string contentType = undefined, headers = undefined) {
                        return this.WithType("POST", url, body, contentType, headers)
                    }

                    Put(string url, body, string contentType = undefined, headers = undefined) {
                        return this.WithType("PUT", url, body, contentType, headers)
                    }

                    WithType(string method, string url, body, string contentType, headers) {
                        var h = new Http.Headers()
                        if (headers != undefined) {
                            for (var i = 0; i < headers.Count; i++) { h.Add(headers.names[i], headers.values[i]) }
                        }
                        if (contentType != undefined) { h.Set("Content-Type", contentType) }
                        else if (!h.Has("Content-Type")) {
                            if (body is of string) { h.Set("Content-Type", "text/plain; charset=utf-8") }
                            else { h.Set("Content-Type", "application/octet-stream") }
                        }
                        return this.Request(method, url, body, h)
                    }

                    // Sends the request and returns the Http.Response (redirects are followed unless `followRedirects` is false).
                    Request(string method, string url, body, headers) {
                        var current = url
                        var verb = method
                        var payload = body
                        var redirects = 0
                        while (true) {
                            var parsed = Http.Url.Parse(current)
                            var response = this.Once(verb, parsed, payload, headers)
                            var location = response.headers.Get("Location")
                            var s = response.status
                            var redirect = s == 301 || s == 302 || s == 303 || s == 307 || s == 308
                            if (this.followRedirects && redirect && location != undefined) {
                                redirects = redirects + 1
                                if (redirects > this.maxRedirects) { throw new Http.HttpException("More than " + this.maxRedirects + " redirects.", 3) }
                                current = parsed.Resolve(location)
                                if ((s == 301 || s == 302 || s == 303) && verb != "HEAD") {
                                    verb = "GET"
                                    payload = undefined
                                }
                                continue
                            }
                            response.url = current
                            return response
                        }
                    }

                    // One request and its response over a connection of its own.
                    Once(string method, Url url, body, headers) {
                        var bytes = undefined
                        if (body != undefined) { bytes = Http.Response.Bytes(body) }
                        var h = new Http.Headers()
                        h.Set("Host", url.HostHeader())
                        h.Set("User-Agent", this.userAgent)
                        h.Set("Accept", "*/*")
                        h.Set("Connection", "close")
                        for (var i = 0; i < this.headers.Count; i++) { h.Set(this.headers.names[i], this.headers.values[i]) }
                        if (headers != undefined) {
                            for (var j = 0; j < headers.Count; j++) { h.Set(headers.names[j], headers.values[j]) }
                        }
                        if (bytes != undefined) { h.Set("Content-Length", "" + bytes.length) }
                        else if (method == "POST" || method == "PUT") { h.Set("Content-Length", "0") }
                        var stream = Http.Transport.Open(url, this.timeout, this.tls)
                        try {
                            stream.ReadTimeout = this.timeout
                            stream.WriteTimeout = this.timeout
                            Http.Wire.Write(stream, method + " " + url.path + " HTTP/1.1", h, bytes)
                            var reader = new Http.Reader(stream)
                            var line = reader.ReadLine(8192)
                            while (line != undefined && line.Length == 0) { line = reader.ReadLine(8192) }
                            if (line == undefined) { throw new Http.HttpException("The server closed the connection without an answer.", 2) }
                            var parts = line.Split(" ")
                            if (parts.length < 2 || !parts[0].StartsWith("HTTP/")) { throw new Http.HttpException("Not an HTTP answer: '" + line + "'.", 2) }
                            var status = Http.Num.Parse(parts[1])
                            if (status < 100 || status > 599) { throw new Http.HttpException("A bad status code: '" + parts[1] + "'.", 2) }
                            var response = new Http.Response(status)
                            var reason = ""
                            for (var k = 2; k < parts.length; k++) {
                                if (k > 2) { reason = reason + " " }
                                reason = reason + parts[k]
                            }
                            if (reason.Length > 0) { response.reason = reason }
                            Http.Wire.ReadHeaders(reader, response.headers)
                            if (method != "HEAD" && !Http.Status.NoBody(status)) {
                                response.body = Http.Wire.ReadBody(reader, response.headers, true, this.maxBodyBytes)
                            }
                            stream.Close()
                            return response
                        } catch (Http.HttpException e) {
                            stream.Close()
                            throw e
                        } catch (Net.NetException e) {
                            stream.Close()
                            throw e
                        }
                    }
                }

                // A route: method ("*" = any), path (a trailing "*" matches every path that starts with the text before it) and the lambda that answers.
                class RouteEntry {
                    string method
                    string path
                    var handler

                    construct(string method, string path, handler) {
                        this.method = method
                        this.path = path
                        this.handler = handler
                    }

                    bool Matches(string requestMethod, string requestPath) {
                        // (a HEAD request is answered by the route for GET, without the body)
                        if (this.method != "*" && this.method != requestMethod && !(this.method == "GET" && requestMethod == "HEAD")) { return false }
                        if (this.path.EndsWith("*")) { return requestPath.StartsWith(this.path.Substring(0, this.path.Length - 1)) }
                        return this.path == requestPath
                    }
                }

                // A small HTTP server: one connection after the other. `Route(method, path, lambda)` where the lambda gets the Http.Request and returns an Http.Response (a text becomes a
                // 200 text response, undefined a 204).
                class Server {
                    var listener
                    List routes
                    var fallback
                    var onError
                    var tlsServer
                    int readTimeout
                    int maxBodyBytes
                    bool stopped

                    // new Http.Server("127.0.0.1", 8080) - host "" listens on every interface, port 0 takes a free port (see Port).
                    construct(string host, int port, int backlog = 16) {
                        this.routes = new List()
                        this.fallback = undefined
                        this.onError = undefined
                        this.tlsServer = undefined
                        this.readTimeout = 10000
                        this.maxBodyBytes = 1048576
                        this.stopped = false
                        this.listener = new Net.TcpListener(host, port, backlog)
                    }

                    int Port { get { return this.listener.Port } }

                    Route(string method, string path, handler) {
                        var entry = new Http.RouteEntry(method, path, handler)
                        entry.TakeTo(this)
                        this.routes.Add(entry)
                    }

                    // The answer to every request that no route matches (default: 404).
                    Fallback(handler) { this.fallback = handler }

                    // Called with the exception when a route's lambda throws (the client gets a 500).
                    OnError(handler) { this.onError = handler }

                    // Serves HTTPS: the certificate (with its chain) and the private key as PEM text. Needs the tls package's support in this build (Tls.Support.Available()).
                    UseTls(string certPem, string keyPem) { this.tlsServer = new Tls.Server(certPem, keyPem) }

                    Stop() { this.stopped = true }

                    // Waits up to `timeout` for a connection and answers it; true if one was served, false when the time ran out.
                    ServeOne(timeout = undefined) {
                        var connection = this.listener.TryAccept(timeout)
                        if (connection == undefined) { return false }
                        if (this.tlsServer != undefined) {
                            try {
                                connection = this.tlsServer.Accept(connection, this.readTimeout)
                            } catch (Net.NetException e) {
                                return true   // a client that did not complete the handshake: nothing to answer
                            }
                        }
                        this.Handle(connection)
                        return true
                    }

                    // Serves until Stop() is called (from a route, or from another fire thread through its own channel - there is no shared state).
                    Run() {
                        while (!this.stopped) { this.ServeOne(250) }
                    }

                    Handle(connection) {
                        try {
                            connection.ReadTimeout = this.readTimeout
                            connection.WriteTimeout = this.readTimeout
                            var response = undefined
                            var head = false
                            try {
                                var request = this.ReadRequest(connection)
                                if (request == undefined) { connection.Close(); return }
                                head = request.method == "HEAD"
                                response = this.Dispatch(request)
                            } catch (Http.HttpException e) {
                                var status = 400
                                if (e.code == 4) { status = 413 }
                                response = Http.Response.Error(status, e.message)
                            } catch (Net.TimeoutException e) {
                                response = Http.Response.Error(408)
                            }
                            this.Send(connection, response, head)
                        } catch (Net.NetException e) {
                            // the client went away
                        }
                        connection.Close()
                    }

                    // The request, or undefined if the client closed without sending one.
                    ReadRequest(connection) {
                        var reader = new Http.Reader(connection)
                        var line = reader.ReadLine(8192)
                        while (line != undefined && line.Length == 0) { line = reader.ReadLine(8192) }
                        if (line == undefined) { return undefined }
                        var parts = line.Split(" ")
                        if (parts.length != 3 || !parts[2].StartsWith("HTTP/")) { throw new Http.HttpException("A bad request line: '" + line + "'.", 2) }
                        var request = new Http.Request(parts[0], parts[1])
                        request.version = parts[2]
                        request.remoteHost = connection.RemoteHost
                        Http.Wire.ReadHeaders(reader, request.headers)
                        request.body = Http.Wire.ReadBody(reader, request.headers, false, this.maxBodyBytes)
                        return request
                    }

                    Dispatch(request) {
                        var result = undefined
                        try {
                            var handler = this.fallback
                            for (var i = 0; i < this.routes.count; i++) {
                                var route = this.routes[i]
                                if (route.Matches(request.method, request.path)) {
                                    handler = route.handler
                                    break
                                }
                            }
                            if (handler == undefined) { return Http.Response.Error(404) }
                            result = handler(request)
                        } catch (e) {
                            var report = this.onError
                            if (report != undefined) { report(e) }
                            return Http.Response.Error(500)
                        }
                        if (result == undefined) { return new Http.Response(204) }
                        if (result is of string) { return Http.Response.FromText(result) }
                        return result
                    }

                    Send(connection, response, bool head) {
                        var h = new Http.Headers()
                        for (var i = 0; i < response.headers.Count; i++) { h.Add(response.headers.names[i], response.headers.values[i]) }
                        h.Set("Content-Length", "" + response.body.length)
                        h.Set("Connection", "close")
                        if (!h.Has("Server")) { h.Set("Server", "fire-http/1") }
                        var body = response.body
                        if (head || Http.Status.NoBody(response.status)) { body = undefined }
                        Http.Wire.Write(connection, "HTTP/1.1 " + response.status + " " + response.reason, h, body)
                    }

                    Close() {
                        this.listener.Close()
                        if (this.tlsServer != undefined) { this.tlsServer.Close() }
                    }
                }
            }
            """;
    }
}
