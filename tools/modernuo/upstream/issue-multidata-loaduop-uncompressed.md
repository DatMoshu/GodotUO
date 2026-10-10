# MultiData.LoadUOP never reads an uncompressed entry

Issue text, ready to file on modernuo/ModernUO once the bundle is reviewed.
Checked against `d4531cd94` (the GUO pin); the code is unchanged there.

---

**Title:** MultiData.LoadUOP parses stale buffer contents for uncompressed MultiCollection.uop entries

**Where:** `Projects/Server/Client/MultiData.cs`, `LoadUOP`, the `else` branch of
`if (entry.Compressed)`.

**What happens:** for an entry whose compression flag is 0, the loader sets

```csharp
data = buffer.AsSpan(0, entry.Size);
```

without reading the entry from the stream. `buffer` still holds the previous
compressed entry (or nothing, if the first entry is the uncompressed one), so the
multi is parsed from the wrong bytes. With a real file this fails at boot:

```
ArgumentOutOfRangeException: Cannot seek to position ... beyond buffer length
   at Server.MultiData.LoadUOP(...)
```

**Why it rarely shows:** every entry in a stock `MultiCollection.uop` is
zlib-compressed. Any tool that writes a multi entry uncompressed (the UOP format
allows it; the flag is per entry) produces a file the client reads and the server
cannot.

**Reproduce:** take a stock `MultiCollection.uop`, rewrite one entry stored
uncompressed (flag 0, compressed size = size), boot the server.

**Suggested fix:** read the entry on the uncompressed path, the same way the
compressed path does:

```csharp
else
{
    if (stream.Read(buffer.AsSpan(0, entry.Size)) != entry.Size)
    {
        throw new FileLoadException($"Error loading file {stream.Name}.");
    }

    data = buffer.AsSpan(0, entry.Size);
}
```

---

GUO does not carry a patch for this: its multi writer stores entries compressed.
