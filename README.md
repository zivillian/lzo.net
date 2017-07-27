[![Build status](https://ci.appveyor.com/api/projects/status/bboaj84n93k6q733?svg=true)](https://ci.appveyor.com/project/zivillian/lzo-net) [![Nuget version](https://img.shields.io/nuget/v/lzo.net.svg)](https://www.nuget.org/packages/lzo.net)

# lzo.net

This is an implementation of the [lzo](https://www.oberhumer.com/opensource/lzo/) decoder in plain c#/.NET (without P/Invoke, `fixed` or `unchecked`).

The first version was based on the decoder from [ffmpeg](https://ffmpeg.org/doxygen/3.1/lzo_8c_source.html), but was replaced with a rewrite based on the documentation from [kernel.org](https://www.kernel.org/doc/Documentation/lzo.txt).

## Features

Currently only decompression is supported.

## License

The early versions contain a port from ffmpeg, which is licensed under LGPL v2.1. Later version do not contain any LGPL code anymore, so the license was switched to MIT. Details can be found in the LICENSE file for each version.

## Usage

Install from [nuget](https://www.nuget.org/packages/lzo.net).

```csharp
using (var compressed = File.OpenRead("File.lzo"))
using (var decompressed = new LzoStream(compressed, CompressionMode.Decompress))
{
    decompressed.Read(buffer, offset, count);
    ...
}
```

## Performance

The code was optimized as much as possible but since it's plain .NET it is of course slower then the native c version. Benchmarks (on the test files) show a performance penalty around factor 4. If you require the best performance, consider using another library which wraps the native version.

Please open an issue, if you've found a way to make the decoder faster.