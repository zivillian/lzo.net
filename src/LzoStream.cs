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
    /// <summary>
    /// Wrapper Stream for lzo compression
    /// </summary>
    public class LzoStream:Stream
    {
        private readonly Stream _base;
        private long? _length;
        private long _inputPosition;
        private readonly long _inputLength;
        private byte[] _decoded;
        private const int MaxWindowSize = (1 << 14) + ((255 & 8) << 11) + (255 << 6) + (255 >> 2);
        private readonly RingBuffer _window = new RingBuffer(MaxWindowSize);
        private long _position;
        private int _instruction;
        private LzoState _lzoState;

        private enum LzoState
        {
            /// <summary>
            /// last instruction did not copy any literal 
            /// </summary>
            ZeroCopy = 0,
            /// <summary>
            /// last instruction used to copy between 1 literal 
            /// </summary>
            SmallCopy1 = 1,
            /// <summary>
            /// last instruction used to copy between 2 literals 
            /// </summary>
            SmallCopy2 = 2,
            /// <summary>
            /// last instruction used to copy between 3 literals 
            /// </summary>
            SmallCopy3 = 3,
            /// <summary>
            /// last instruction used to copy 4 or more literals 
            /// </summary>
            LargeCopy = 4
        }

        /// <summary>
        /// creates a new lzo stream for decompression
        /// </summary>
        /// <param name="stream">the compressed stream</param>
        /// <param name="mode">currently only decompression is supported</param>
        public LzoStream(Stream stream, CompressionMode mode)
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

        private void Copy(byte[] buffer, int offset, int count)
        {
            if (count > _inputLength - _inputPosition)
                throw new EndOfStreamException();
            while (count > 0)
            {
                var read = _base.Read(buffer, offset, count);
                if (read == 0)
                    throw new EndOfStreamException();
                _window.Write(buffer, offset, read);
                _inputPosition += read;
                offset += read;
                count -= read;
            }
        }

        private int Decode(byte[] buffer, int offset, int count)
        {
            Debug.Assert(_decoded == null);
            int read;
            if (_instruction <= 15)
            {
                /*
                 * Depends on the number of literals copied by the last instruction.                 
                 */
                int distance;
                int length;
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
                        length = 3;
                        if (_instruction != 0)
                        {
                            length += _instruction;
                        }
                        else
                        {
                            length += 15 + ReadLength();
                        }
                        if (length > count)
                        {
                            _decoded = new byte[length];
                            Copy(_decoded, 0, length);
                            read = 0;
                        }
                        else
                        {
                            Copy(buffer, offset, length);
                            read = length;
                        }
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

                        read = CopyFromRingBuffer(buffer, offset, count, distance, 2, _instruction & 0x3);
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

                        read = CopyFromRingBuffer(buffer, offset, count, distance, 3, _instruction & 0x3);
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
                int length;
                var l = _instruction & 0x7;
                if (l == 0)
                {
                    length = 2 + 7 + ReadLength();
                }
                else
                {
                    length = 2 + l;
                }
                var s = GetByte();
                var d = GetByte() << 8;
                d = (d | s) >> 2;
                var distance = 16384 + ((_instruction & 0x8) << 11) | d;
                if (distance == 16384)
                    return -1;

                read = CopyFromRingBuffer(buffer, offset, count, distance, length, s & 0x3);
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
                int length;
                var l = _instruction & 0x1f;
                if (l == 0)
                {
                    length = 2 + 31 + ReadLength();
                }
                else
                {
                    length = 2 + l;
                }
                var s = GetByte();
                var d = GetByte() << 8;
                d = (d | s) >> 2;
                var distance = d + 1;

                read = CopyFromRingBuffer(buffer, offset, count, distance, length, s & 0x3);
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
                var length = 3 + ((_instruction >> 5) & 0x1);
                var distance = (GetByte() << 3) + ((_instruction >> 2) & 0x7) + 1;

                read = CopyFromRingBuffer(buffer, offset, count, distance, length, _instruction & 0x3);
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
                var length = 5 + ((_instruction >> 5) & 0x3);
                var distance = (GetByte() << 3) + ((_instruction & 0x1c) >> 2) + 1;

                read = CopyFromRingBuffer(buffer, offset, count, distance, length, _instruction & 0x3);
            }

            _instruction = GetByte();
            _position += read;
            return read;
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

        private int CopyFromRingBuffer(byte[] buffer, int offset, int count, int distance, int copy, int state)
        {
            var result = copy + state;
            if (count < result)
            {
                _decoded = new byte[result];
                CopyFromRingBuffer(_decoded, 0, _decoded.Length, distance, copy, state);
                return 0;
            }
            
            var size = copy;
            if (copy > distance)
            {
                size = distance;
                _window.Seek(-distance);
                var read = _window.Read(buffer, offset, size);
                if (read == 0)
                    throw new EndOfStreamException();
                Debug.Assert(read == size);
                _window.Seek(distance - read);
                _window.Write(buffer, offset, read);
                copy -= read;
                var copies = copy / distance;
                for (int i = 0; i < copies; i++)
                {
                    _window.Write(buffer, offset, read);
                    Buffer.BlockCopy(buffer, offset, buffer, offset + read, read);
                    offset += read;
                    copy -= read;
                }
                offset += read;
            }
            while (copy > 0)
            {
                _window.Seek(-distance);
                if (copy < size)
                    size = copy;
                var read = _window.Read(buffer, offset, size);
                if (read == 0)
                    throw new EndOfStreamException();
                _window.Seek(distance - read);
                _window.Write(buffer, offset, read);
                offset += read;
                copy -= read;
            }
            if (state > 0)
            {
                Copy(buffer, offset, state);
            }
            _lzoState = (LzoState)state;
            return result;
        }

        private int ReadInternal(byte[] buffer, int offset, int count)
        {
            if (_length.HasValue && _position >= _length)
                return -1;
            int read;
            if (_decoded != null)
            {
                var decodedLength = _decoded.Length;
                if (count > decodedLength)
                {
                    Buffer.BlockCopy(_decoded, 0, buffer, offset, decodedLength);
                    _decoded = null;
                    return decodedLength;
                }
                Buffer.BlockCopy(_decoded, 0, buffer, offset, count);
                if (decodedLength > count)
                {
                    var remaining = new byte[decodedLength - count];
                    Buffer.BlockCopy(_decoded, count, remaining, 0, remaining.Length);
                    _decoded = remaining;
                }
                else
                {
                    _decoded = null;
                }
                return count;
            }
            if ((read = Decode(buffer, offset, count)) < 0)
            {
                _length = _position;
                return -1;
            }
            return read;
        }

        #region wrapped stream methods

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

        #endregion
    }
}
