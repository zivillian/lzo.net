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
        private readonly int _size;

        public RingBuffer(int size)
        {
            _buffer = new byte[size];
            _size = size;
        }

        public void Seek(int offset)
        {
            _position += offset;
            if (_position > _size)
            {
                _position %= _size;
                return;
            }
            while (_position < 0)
            {
                _position += _size;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            var cnt = count;
            if (count < 10 && (_position + count) < _size)
            {
                do
                {
                    buffer[offset++] = _buffer[_position++];
                } while (--cnt > 0);
                return count;
            }
            while (cnt > 0)
            {
                var copy = _size - _position;
                if (copy > cnt)
                {
                    copy = cnt;
                }
                Buffer.BlockCopy(_buffer, _position, buffer, offset, copy);
                _position = (_position + copy);
                if (_position >= _size)
                    _position %= _size;
                cnt -= copy;
                offset += copy;
            }
            return count;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            if (count == 0) return;
            if (count < 10 && (_position + count) < _size)
            {
                do
                {
                    _buffer[_position++] = buffer[offset++];
                } while (--count > 0);
            }
            else
                while (count > 0)
            {
                var cnt = _size - _position;
                if (cnt > count)
                    cnt = count;
                Buffer.BlockCopy(buffer, offset, _buffer, _position, cnt);
                _position = (_position + cnt);
                if (_position >= _size)
                    _position %= _size;
                offset += cnt;
                count -= cnt;
            }
        }
    }
}
