# XnbStringTool

A standalone reader/writer for the `Dictionary<string,string>` XNB assets
under Stardew Valley's `Content/Strings/` folder (item names, UI text,
dialogue tables, etc.) -- reverse-engineered from the actual on-disk files
plus MonoGame's own source (see `reference/`, fetched from
`github.com/MonoGame/MonoGame`, dual LGPL2.1/MS-PL licensed) to confirm the
exact wire format rather than guessing. Ported MonoGame's `LzxDecoder` for
decompression (`LzxDecoder.cs`) so this tool has no runtime dependency on
the game's own assemblies (which are x64-only and won't load into this
Mac's arm64 .NET SDK).

Writing intentionally only ever produces **uncompressed** XNB output --
compression is optional in the format (a header flag bit), so an
uncompressed file loads identically. That avoids needing an LZX *encoder*,
which nothing here requires.

## Usage

```
dotnet run -- read <input.xnb> <output.json>
dotnet run -- write <input.json> <output.xnb>
dotnet run -- dump-dir <inputDir> <outputDir>   # converts every *.xnb found, mirroring the directory structure
```

`write` reads a JSON object shaped like `{ "entries": { "key": "value", ... } }`
(or a bare `{ "key": "value" }` object).

## Tests

`../XnbStringTool.Tests` (xunit): round-trip tests with synthetic data, plus
"golden master" tests against the real installed game (hardcoded path to
this machine's Steam install; see `RealGameDataTests.cs`).

```
cd ../XnbStringTool.Tests && dotnet test
```
