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

namespace lzo.net
{
    public class RingBuffer
    {
        private readonly byte[] _buffer;
        private int _position;

        public RingBuffer(int size)
        {
            _buffer = new byte[size];
        }

        public int Position
        {
            get { return _position; }
            set
            {
                var bufferLength = _buffer.Length;
                _position = (value + bufferLength);
                if (_position >= bufferLength)
                    _position %= bufferLength;
            }
        }

        private int Remaining
        {
            get { return _buffer.Length - _position; }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var cnt = count;
            while (cnt > 0)
            {
                var bufferLength = _buffer.Length;
                var copy = bufferLength - _position;
                if (copy > cnt)
                {
                    copy = cnt;
                }
                Buffer.BlockCopy(_buffer, _position, buffer, offset, copy);
                _position = (_position + copy);
                if (_position >= bufferLength)
                    _position %= bufferLength;
                cnt -= copy;
                offset += copy;
            }
            return count;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var bufferLength = _buffer.Length;
                var cnt = bufferLength - _position;
                if (cnt > count)
                    cnt = count;
                Buffer.BlockCopy(buffer, offset, _buffer, _position, cnt);
                _position = (_position + cnt);
                if (_position >= bufferLength)
                    _position %= bufferLength;
                offset += cnt;
                count -= cnt;
            }
        }
    }
}
