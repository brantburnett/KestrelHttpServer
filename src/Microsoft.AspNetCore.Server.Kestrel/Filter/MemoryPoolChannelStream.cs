// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Http;
using Microsoft.AspNetCore.Server.Kestrel.Infrastructure;

namespace Microsoft.AspNetCore.Server.Kestrel.Filter
{
    public class MemoryPoolChannelStream : Stream
    {
        private readonly static Task<int> _initialCachedTask = Task.FromResult(0);

        private readonly IReadableChannel _input;
        private readonly IWritableChannel _output;

        private Task<int> _cachedTask = _initialCachedTask;

        public MemoryPoolChannelStream(IReadableChannel input, IWritableChannel output)
        {
            _input = input;
            _output = output;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length
        {
            get
            {
                throw new NotSupportedException();
            }
        }

        public override long Position
        {
            get
            {
                throw new NotSupportedException();
            }
            set
            {
                throw new NotSupportedException();
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // ValueTask uses .GetAwaiter().GetResult() if necessary
            // https://github.com/dotnet/corefx/blob/f9da3b4af08214764a51b2331f3595ffaf162abe/src/System.Threading.Tasks.Extensions/src/System/Threading/Tasks/ValueTask.cs#L156
            return ReadAsync(new ArraySegment<byte>(buffer, offset, count)).Result;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var task = ReadAsync(new ArraySegment<byte>(buffer, offset, count));

            if (task.IsCompletedSuccessfully)
            {
                if (_cachedTask.Result != task.Result)
                {
                    // Needs .AsTask to match Stream's Async method return types
                    _cachedTask = task.AsTask();
                }
            }
            else
            {
                // Needs .AsTask to match Stream's Async method return types
                _cachedTask = task.AsTask();
            }

            return _cachedTask;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _output.WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            // TODO: Handle cancellation
            return _output.WriteAsync(buffer, offset, count);
        }

        public override void Flush()
        {
            // No-op since writes are immediate.
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            // No-op since writes are immediate.
            return TaskUtilities.CompletedTask;
        }

        private ValueTask<int> ReadAsync(ArraySegment<byte> buffer)
        {
            return _input.ReadAsync(buffer.Array, buffer.Offset, buffer.Count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _output.Close();
            }

            base.Dispose(disposing);
        }
    }
}
