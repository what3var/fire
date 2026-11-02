# Hash functions (`#import "hash"`)

The package `fire-hash` computes digests, checksums and message authentication codes. Everything is plain C in the package (no OpenSSL, no mbedTLS), so
the results are the same in the virtual machine, in a native build, with TinyCC and on a board.

```
#import "hash"

print(Hash.Sha256.HexOf("abc"))                 // ba7816bf...f20015ad (a string is hashed as UTF-8)
var digest = Hash.Sha256.Of(bytes)               // 32 bytes
var fileHash = Hash.Sha256.HexOf(IO.File.OpenRead("big.iso"))   // a stream is read to its end

var h = new Hash.Sha512()                        // piece by piece
h.Update(buffer, 0, n)
h.UpdateText("more")
print(h.FinishHex())                             // Finish starts the hasher anew

print(Hash.Crc32.Of("123456789"))                // 3421780262 (0xcbf43926)
print(Hash.Hmac.HexOf(Hash.Algorithm.Sha256, key, message))
```

## Reference

| Member | Meaning |
|---|---|
| `Hash.Md5`, `Hash.Sha1`, `Hash.Sha256`, `Hash.Sha384`, `Hash.Sha512` | `static byte[] Of(data)`, `static string HexOf(data)`; `new Hash.Sha256()` is a hasher |
| `Hash.Crc32`, `Hash.Adler32` | `static int Of(data)`, `static string HexOf(data)`; a hasher with `int Value` (the checksum so far, without starting anew) |
| `Hash.Hasher(Hash.Algorithm)` | the base of all: `Update(bytes, offset = 0, count = -1)`, `UpdateText(string)`, `UpdateStream(IO.Stream)`, `UpdateAny(data)`, `byte[] Finish()`, `string FinishHex()`, `Reset()`, `Size`, `Algorithm`; `static Compute(algorithm, data)` |
| `Hash.Algorithm` | `Md5`, `Sha1`, `Sha256`, `Sha384`, `Sha512`, `Crc32`, `Adler32` |
| `Hash.Hmac(algorithm, key)` | HMAC (RFC 2104) with MD5 .. SHA-512; `key` is bytes or a string; `Update`/`UpdateText`/`UpdateStream`, `Finish`/`FinishHex` (starts anew with the same key); `static Of(algorithm, key, data)`, `static HexOf(...)` |
| `Hash.Hex.Encode(bytes)` / `Hash.Hex.Decode(text)` | lower-case hex digits; decoding takes either case and skips spaces, `:` and `-` between the bytes |
| `Hash.Bytes.Equal(a, b)` | the same bytes? in a time that does not depend on where they differ (for digests and MACs) |

`data` is a byte buffer, a string (hashed as UTF-8; a lone surrogate as U+FFFD, like `IO.Utf8.GetBytes`) or an `IO.Stream` (read in pieces of 64 KB until
its end). Wrong arguments (an offset outside of the buffer, a hex digit that is none, a checksum as the function of an HMAC) throw an `ArgumentException`.

The digests of CRC-32 and Adler-32 as bytes (`Finish`, `HexOf`) are big endian - the way the numbers are written; Zip and gzip store CRC-32 little endian.

## Implementation

`packages/hash/native/fire_bridge_hash.h` (C99). The state of a hasher lives in a byte buffer of the fire object (`__HashInit` makes it, `__HashUpdate`
changes it in place, `__HashFinal` takes the digest and starts it anew): there is no handle table and nothing to free when a program ends. A buffer that is not
such a state is refused. Checked against the test vectors of FIPS 180-4, RFC 1321, RFC 2202 and RFC 4231 and against Python's `hashlib`/`zlib` for many
lengths and splits (`packages/hash/testing`).
