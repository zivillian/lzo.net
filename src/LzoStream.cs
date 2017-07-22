/*
 * LZO 1x decompression
 * copyright (c) 2006 Reimar Doeffinger
 * copyright (c) 2017 Bianco Veigel
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
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace lzo.net
{
    public class LzoStream:Stream
    {
        private Stream _base;
        private long? _length;
        private int _state;
        private byte _x;
        private long _inputPosition;
        private readonly long _inputLength;
        private byte[] _decoded;
        private const int MaxWindowSize = (1 << 14) + ((255 & 8) << 11) + (255 << 6) + (255 >> 2);
        private readonly RingBuffer _window = new RingBuffer(MaxWindowSize);
        private long _position;

        public LzoStream(Stream stream, CompressionMode mode)
        {
            if (mode != CompressionMode.Decompress)
                throw new NotSupportedException("Compression is not supported");
            if (!stream.CanRead)
                throw new ArgumentException("write-only stream cannot be used for decompression");
            _base = stream;
            _inputLength = _base.Length;
            _x = GetByte();
            if (_x > 17)
            {
                _decoded = Copy(_x - 17);
                _x = GetByte();
                if (_x < 16)
                    throw new Exception();
            }
        }
        
        private byte GetByte()
        {
            var result = _base.ReadByte();
            _inputPosition++;
            if (result == -1)
                throw new EndOfStreamException();
            return (byte)result;
        }

        private byte[] Copy(int count)
        {
            if (count > _inputLength - _inputPosition)
                throw new EndOfStreamException();
            var buffer = new byte[count];

            while (count > 0)
            {
                var read = _base.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    throw new EndOfStreamException();
                _window.Write(buffer, 0, read);
                _inputPosition += read;
                count -= read;
            }
            return buffer;
        }

        private bool Decode()
        {
            Debug.Assert(_decoded == null);
            int cnt, back;
            if (_x > 15)
            {
                if (_x > 63)
                {
                    cnt = (_x >> 5) - 1;
                    back = (GetByte() << 3) + ((_x >> 2) & 7) + 1;
                }
                else if (_x > 31)
                {
                    cnt = get_len(_x, 31);
                    _x = GetByte();
                    back = (GetByte() << 6) + (_x >> 2) + 1;
                }
                else
                {
                    cnt = get_len(_x, 7);
                    back = (1 << 14) + ((_x & 8) << 11);
                    _x = GetByte();
                    back += (GetByte() << 6) + (_x >> 2);
                    if (back == (1 << 14))
                    {
                        if (cnt != 1)
                            throw new Exception();
                        return false;
                    }
                }
            }
            else if (_state == 0)
            {
                cnt = get_len(_x, 15);
                var data =Copy(cnt + 3);
                Append(data);
                _x = GetByte();
                if (_x > 15)
                {
                    _position += _decoded.Length;
                    return true;
                }
                cnt = 1;
                back = (1 << 11) + (GetByte() << 2) + (_x >> 2) + 1;
            }
            else
            {
                cnt = 0;
                back = (GetByte() << 2) + (_x >> 2) + 1;
            }
            copy_backptr(back, cnt + 2);
            _state = cnt = _x & 3;
            if (cnt > 0)
            {
                var data = Copy(cnt);
                Append(data);
            }
            _x = GetByte();
            _position += _decoded.Length;
            return true;
        }

        private void Append(byte[] data)
        {
            if (_decoded == null)
            {
                _decoded = data;
                return;
            }
            var result = new byte[_decoded.Length + data.Length];
            Buffer.BlockCopy(_decoded, 0, result, 0, _decoded.Length);
            Buffer.BlockCopy(data, 0, result, _decoded.Length, data.Length);
            _decoded = result;
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
            var size = cnt;
            byte[] buffer;
            if (cnt > back)
            {
                size = cnt % back;
                buffer = new byte[back];
                _window.Position -= back;
                var read = _window.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    throw new EndOfStreamException();
                Debug.Assert(read == buffer.Length);
                _window.Position += back - read;
                var copies = cnt / back;
                for (int i = 0; i < copies; i++)
                {
                    _window.Write(buffer, 0, read);
                    Append(buffer);
                    cnt -= read;
                }
            }
            buffer = new byte[size];
            while (cnt > 0)
            {
                _window.Position -= back;
                if (cnt < buffer.Length)
                    buffer = new byte[cnt];
                var read = _window.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    throw new EndOfStreamException();
                _window.Position += back - read;
                _window.Write(buffer, 0, read);
                Append(buffer);
                cnt -= read;
            }
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek {get { return false; }}

        public override bool CanWrite {get { return false; }}

        public override long Length
        {
            get
            {
                if (_length.HasValue)
                    return _length.Value;
                throw new NotSupportedException();
            }
        }

        public override long Position
        {
            get { return _position; }
            set { _position = value; }
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_length.HasValue && _position >= _length)
                return 0;
            var result = 0;
            while (count > 0)
            {
                var read = ReadInternal(buffer, offset, count);
                if (read == -1)
                    return result;
                result += read;
                offset += read;
                count -= read;
            }
            return result;
        }

        private int ReadInternal(byte[] buffer, int offset, int count)
        {
            if (_length.HasValue && _position >= _length)
                return -1;
            var read = 0;
            if (_decoded != null)
            {
                if (count > _decoded.Length)
                {
                    Buffer.BlockCopy(_decoded, 0, buffer, offset, _decoded.Length);
                    read = _decoded.Length;
                    _decoded = null;
                }
                else
                {
                    Buffer.BlockCopy(_decoded, 0, buffer, offset, count);
                    if (_decoded.Length - count > 0)
                    {
                        var remaining = new byte[_decoded.Length - count];
                        Buffer.BlockCopy(_decoded, count, remaining, 0, remaining.Length);
                        _decoded = remaining;
                    }
                    else
                    {
                        _decoded = null;
                    }
                    return count;
                }
            }
            if (!Decode())
            {
                _length = _position;
                if (read != 0)
                    return read;
                return -1;
            }
            Debug.Assert(_decoded != null);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            _length = value;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new InvalidOperationException("cannot write to readonly stream");
        }
    }
}
