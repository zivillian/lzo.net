/*
 * LZO 1x decompression
 * copyright (c) 2006 Reimar Doeffinger
 *
 * This file was ported from FFmpeg by Bianco Veigel
 * 
 * FFmpeg is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.
 *
 * FFmpeg is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with FFmpeg; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA 02110-1301 USA
 */

using System;
using System.IO;

namespace lzo.net
{
    public class Lzo1xDecoder
    {
        private readonly Stream _input;
        private readonly Stream _output;
        private readonly int _bufferSize;

        public Lzo1xDecoder(Stream input, Stream output)
        {
            
        }
        public Lzo1xDecoder(Stream input, Stream output, int maxBufferSize)
        {
            if (!output.CanWrite)
                throw new ArgumentException("output stream cannot be readonly", nameof(output));
            if (!input.CanRead)
                throw new ArgumentException("input stream must be readable", nameof(input));
            if (maxBufferSize <= 0)
                throw new ArgumentException("buffer cannot be negative or empty", nameof(maxBufferSize));
            _input = input;
            _output = output;
            _bufferSize = maxBufferSize;
        }

        public void Decode()
        {
            int state = 0;
            var x = GetByte();
            if (x > 17)
            {
                Copy(x - 17);
                x = GetByte();
                if (x < 16)
                    throw new Exception();
            }
            while (true)
            {
                int cnt, back;
                if (x > 15)
                {
                    if (x > 63)
                    {
                        cnt = (x >> 5) - 1;
                        back = (GetByte() << 3) + ((x >> 2) & 7) + 1;
                    }
                    else if (x > 31)
                    {
                        cnt = get_len(x, 31);
                        x = GetByte();
                        back = (GetByte() << 6) + (x >> 2) + 1;
                    }
                    else
                    {
                        cnt = get_len( x, 7);
                        back = (1 << 14) + ((x & 8) << 11);
                        x = GetByte();
                        back += (GetByte() << 6) + (x >> 2);
                        if (back == (1 << 14))
                        {
                            if (cnt != 1)
                                throw new Exception();
                            break;
                        }
                    }
                }
                else if (state == 0)
                {
                    cnt = get_len(x, 15);
                    Copy(cnt + 3);
                    x = GetByte();
                    if (x > 15)
                        continue;
                    cnt = 1;
                    back = (1 << 11) + (GetByte() << 2) + (x >> 2) + 1;
                }
                else
                {
                    cnt = 0;
                    back = (GetByte() << 2) + (x >> 2) + 1;
                }
                copy_backptr(back, cnt + 2);
                state = cnt = x & 3;
                Copy(cnt);
                x = GetByte();
            }
        }

        private byte GetByte()
        {
            var result = _input.ReadByte();
            if (result == -1)
                throw new EndOfStreamException();
            return (byte) result;
        }

        private void Copy(int count)
        {
            if (count < _input.Length-_input.Position)
                throw new EndOfStreamException();
            var buffer = new byte[_bufferSize];

            while (count > 0)
            {
                var read = _input.Read(buffer, 0, Math.Min(count, buffer.Length));
                if (read == 0)
                    throw new EndOfStreamException();
                _output.Write(buffer, 0, read);
                count -= read;
            }
        }

        private int get_len(int x, int mask)
        {
            int cnt = x & mask;
            if (cnt == 0)
            {
                while ((x = GetByte()) == 0)
                {
                    if (cnt >= Int32.MaxValue - 1000)
                    {
                        throw new Exception();
                    }
                    cnt += 255;
                }
                cnt += mask + x;
            }
            return cnt;
        }

        private void copy_backptr(int back, int cnt)
        {
            if (cnt > back)
                throw new NotImplementedException();
            var buffer = new byte[_bufferSize];
            while (cnt > 0)
            {
                _output.Position -= back;
                var read = _output.Read(buffer, 0, Math.Min(cnt, buffer.Length));
                if (read == 0)
                    throw new EndOfStreamException();
                _output.Position += back;
                _output.Write(buffer, 0, read);
                cnt -= read;
            }
        }
    }
}
