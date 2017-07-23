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
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace lzo.net
{
    public class BetterLzoStream:Stream
    {
        private readonly Stream _base;
        private long? _length;
        private long _inputPosition;
        private readonly long _inputLength;
        private byte[] _decoded;
        private const int MaxWindowSize = (1 << 14) + ((255 & 8) << 11) + (255 << 6) + (255 >> 2);
        private readonly RingBuffer _window = new RingBuffer(MaxWindowSize);
        private long _position;

        private enum LzoState
        {
            /// <summary>
            /// last instruction did not copy any literal 
            /// </summary>
            ZeroCopy = 0,
            /// <summary>
            /// last instruction used to copy between 1 to 3 literals 
            /// </summary>
            SmallCopy1 = 1,
            /// <summary>
            /// last instruction used to copy between 1 to 3 literals 
            /// </summary>
            SmallCopy2 = 2,
            /// <summary>
            /// last instruction used to copy between 1 to 3 literals 
            /// </summary>
            SmallCopy3 = 3,
            /// <summary>
            /// last instruction used to copy 4 or more literals 
            /// </summary>
            LargeCopy = 4
        }

        private int _instruction;
        private LzoState _lzoState;
        public BetterLzoStream(Stream stream, CompressionMode mode)
        {
            if (mode != CompressionMode.Decompress)
                throw new NotSupportedException("Compression is not supported");
            if (!stream.CanRead)
                throw new ArgumentException("write-only stream cannot be used for decompression");
            _base = stream;
            _inputLength = _base.Length;
            DecodeFirstByte();
        }

        private void DecodeFirstByte()
        {
            _instruction = GetByte();
            if (_instruction > 15 && _instruction <= 17)
            {
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

            if (_instruction <= 15)
            {
                /*
                 * Depends on the number of literals copied by the last instruction.                 
                 */
                int distance;
                int count;
                switch (_lzoState)
                {
                    case LzoState.ZeroCopy:
                        /*
                         * this encoding will be a copy of 4 or more literal, and must be interpreted
                         * like this :                         * 
                         * 0 0 0 0 L L L L  (0..15)  : copy long literal string
                         * length = 3 + (L ?: 15 + (zero_bytes * 255) + non_zero_byte)
                         * state = 4  (no extra literals are copied)
                         */
                        count = 3;
                        if (_instruction != 0)
                        {
                            count += _instruction;
                        }
                        else
                        {
                            count += 15 + ReadLength();
                        }
                        _decoded = Copy(count);
                        _lzoState = LzoState.LargeCopy;
                        break;
                    case LzoState.SmallCopy1:
                    case LzoState.SmallCopy2:
                    case LzoState.SmallCopy3:
                        /* 
                         * the instruction is a copy of a
                         * 2-byte block from the dictionary within a 1kB distance. It is worth
                         * noting that this instruction provides little savings since it uses 2
                         * bytes to encode a copy of 2 other bytes but it encodes the number of
                         * following literals for free. It must be interpreted like this :
                         * 
                         * 0 0 0 0 D D S S  (0..15)  : copy 2 bytes from <= 1kB distance
                         * length = 2
                         * state = S (copy S literals after this block)
                         * Always followed by exactly one byte : H H H H H H H H
                         * distance = (H << 2) + D + 1
                         */
                        var h = GetByte();
                        distance = (h << 2) + ((_instruction & 0xc) >> 2) + 1;

                        CopyFromRingBuffer(distance, 2, _instruction & 0x3);
                        break;
                    case LzoState.LargeCopy:
                        /*
                         *the instruction becomes a copy of a 3-byte block from the
                         * dictionary from a 2..3kB distance, and must be interpreted like this :
                         * 0 0 0 0 D D S S  (0..15)  : copy 3 bytes from 2..3 kB distance
                         * length = 3
                         * state = S (copy S literals after this block)
                         * Always followed by exactly one byte : H H H H H H H H
                         * distance = (H << 2) + D + 2049
                         */
                        distance = (GetByte() << 2) + ((_instruction & 0xc) >> 2) + 2049;

                        CopyFromRingBuffer(distance, 3, _instruction & 0x3);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            else if (_instruction < 32)
            {
                /*
                 * 0 0 0 1 H L L L  (16..31)
                 * Copy of a block within 16..48kB distance (preferably less than 10B)
                 * length = 2 + (L ?: 7 + (zero_bytes * 255) + non_zero_byte)
                 * Always followed by exactly one LE16 :  D D D D D D D D : D D D D D D S S
                 * distance = 16384 + (H << 14) + D
                 * state = S (copy S literals after this block)
                 * End of stream is reached if distance == 16384
                 */
                int count;
                var l = _instruction & 0x7;
                if (l == 0)
                {
                    count = 2 + 7 + ReadLength();
                }
                else
                {
                    count = 2 + l;
                }
                var s = GetByte();
                var d = GetByte() << 8;
                d = (d | s) >> 2;
                var distance = 16384 + ((_instruction & 0x8) << 11) | d;
                if (distance == 16384)
                    return false;

                CopyFromRingBuffer(distance, count, s & 0x3);
            }
            else if (_instruction < 64)
            {
                /*
                 * 0 0 1 L L L L L  (32..63)
                 * Copy of small block within 16kB distance (preferably less than 34B)
                 * length = 2 + (L ?: 31 + (zero_bytes * 255) + non_zero_byte)
                 * Always followed by exactly one LE16 :  D D D D D D D D : D D D D D D S S
                 * distance = D + 1
                 * state = S (copy S literals after this block)
                 */
                int count;
                var l = _instruction & 0x1f;
                if (l == 0)
                {
                    count = 2 + 31 + ReadLength();
                }
                else
                {
                    count = 2 + l;
                }
                var s = GetByte();
                var d = GetByte() << 8;
                d = (d | s) >> 2;
                var distance = d + 1;

                CopyFromRingBuffer(distance, count, s & 0x3);
            }
            else if (_instruction < 128)
            {
                /*
                 * 0 1 L D D D S S  (64..127)
                 * Copy 3-4 bytes from block within 2kB distance
                 * state = S (copy S literals after this block)
                 * length = 3 + L
                 * Always followed by exactly one byte : H H H H H H H H
                 * distance = (H << 3) + D + 1
                 */
                var count = 3 + ((_instruction >> 5) & 0x1);
                var distance = (GetByte() << 3) + ((_instruction >> 2) & 0x7) + 1;

                CopyFromRingBuffer(distance, count, _instruction & 0x3);
            }
            else
            {
                /*
                 * 1 L L D D D S S  (128..255)
                 * Copy 5-8 bytes from block within 2kB distance
                 * state = S (copy S literals after this block)
                 * length = 5 + L
                 * Always followed by exactly one byte : H H H H H H H H
                 * distance = (H << 3) + D + 1
                 */
                var count = 5 + ((_instruction >> 5) & 0x3);
                var distance = (GetByte() << 3) + ((_instruction & 0x1c) >> 2) + 1;

                CopyFromRingBuffer(distance, count, _instruction & 0x3);
            }

            _instruction = GetByte();
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

        private int ReadLength()
        {
            byte b;
            int length = 0;
            while ((b = GetByte()) == 0)
            {
                if (length >= Int32.MaxValue - 1000)
                {
                    throw new Exception();
                }
                length += 255;
            }
            return length + b;
        }

        private void CopyFromRingBuffer(int distance, int count, int state)
        {
            var size = count;
            byte[] buffer;
            if (count > distance)
            {
                size = count % distance;
                buffer = new byte[distance];
                _window.Position -= distance;
                var read = _window.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    throw new EndOfStreamException();
                Debug.Assert(read == buffer.Length);
                _window.Position += distance - read;
                var copies = count / distance;
                for (int i = 0; i < copies; i++)
                {
                    _window.Write(buffer, 0, read);
                    Append(buffer);
                    count -= read;
                }
            }
            buffer = new byte[size];
            while (count > 0)
            {
                _window.Position -= distance;
                if (count < buffer.Length)
                    buffer = new byte[count];
                var read = _window.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    throw new EndOfStreamException();
                _window.Position += distance - read;
                _window.Write(buffer, 0, read);
                Append(buffer);
                count -= read;
            }
            if (state > 0)
                Append(Copy(state));
            _lzoState = (LzoState)state;
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
