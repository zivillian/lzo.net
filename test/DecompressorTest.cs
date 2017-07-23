//
// Copyright (c) 2017, Bianco Veigel
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace lzo.net.test
{
    public class DecompressorTest
    {
        private readonly ITestOutputHelper _output;
        public DecompressorTest(ITestOutputHelper output)
        {
            _output = output;
        }

        private readonly Dictionary<string, string> _checksums = new Dictionary<string, string>
        {
            {"world95.txt", "9b43f25ddee11084ba66f7e638642e85"},
            {"A10.jpg", "735ba97200122210ee7c3faaa98a701a"},
            {"AcroRd32.exe", "9e0d2a448501bf430984ba041e6658f4"},
            {"english.dic", "930f84ac31e3bd1802d9aba392c475a0"},
            {"FlashMX.pdf", "8bf62ff2eebde7f3f132c6f362328dba"},
            {"FP.LOG", "059d1d86734a3478c29e862bf2026684"},
            {"MSO97.DLL", "e0e632f9bb8cdcd80cf6808c5a65ee57"},
            {"ohs.doc", "6c50dce99fc1f5b9f2499ab8bf310b52"},
            {"rafale.bmp", "2cdd1be2bc018c41db37c65fa53ef6c0"},
            {"vcfiu.hlp", "cbb742459acb755945bcf17dc0c1d8f0"},
        };

        [Fact]
        public void ValidateTestFiles()
        {
            foreach (var checksum in _checksums)
            {
                var filename = Path.Combine(@"..\..\..\data", checksum.Key + ".lzo");
                ValidateFile(filename, checksum.Value);
            }
        }

        private void ValidateFile(string filename, string checksum)
        {
            var sw = new Stopwatch();
            sw.Start();
            var md5 = MD5.Create();
            using (var file = File.OpenRead(filename))
            using (var ms = new MemoryStream())
            using (var lzo = new BetterLzoStream(file, CompressionMode.Decompress))
            {
                lzo.CopyTo(ms);
                ms.Seek(0, SeekOrigin.Begin);
                var hash = md5.ComputeHash(ms);
                var hex = BitConverter.ToString(hash).Replace("-", String.Empty).ToLowerInvariant();
                Assert.Equal(checksum, hex);
            }
            sw.Stop();
            _output.WriteLine($"BetterLzoStream took {sw.ElapsedMilliseconds}ms");
        }
        
        [Fact]
        public void LargeFileTest()
        {
            var enwik8 = "a1fa5ffddb56f4953e226637dabbb36a";
            ValidateFile(@"..\..\..\data\enwik8.lzo", enwik8);
        }
    }
}
