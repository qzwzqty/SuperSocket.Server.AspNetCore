using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using SuperSocket.Channel;
using SuperSocket.ProtoBase;

namespace SuperSocket.Server.AspNetCore
{
    public class KestrelPipeChannel<TPackageInfo> : ChannelBase<TPackageInfo>, IChannel<TPackageInfo>, IChannel, IPipeChannel
    {
        public Pipe In => throw new NotImplementedException();

        public Pipe Out => throw new NotImplementedException();

        public IPipelineFilter PipelineFilter => _pipelineFilter;

        protected SemaphoreSlim SendLock { get; } = new SemaphoreSlim(1, 1);

        protected ILogger Logger { get; }

        protected ChannelOptions Options { get; }

        private IPipelineFilter<TPackageInfo> _pipelineFilter;
        private readonly ConnectionContext _connection;
        private readonly CancellationTokenSource _cts = new();
        private bool _isDetaching = false;
        private Task _readsTask;
        private BlockingCollection<TPackageInfo> _packMessageQueue = new();

        public KestrelPipeChannel(IPipelineFilter<TPackageInfo> pipelineFilter, ChannelOptions options, ConnectionContext connection)
        {
            this._pipelineFilter = pipelineFilter;
            this.Options = options;
            this.Logger = options.Logger;
            this._connection = connection;
        }

        public override void Start()
        {
            _readsTask = ReceivePacketAsync();
            WaitHandleClosing();
        }

        public override async IAsyncEnumerable<TPackageInfo> RunAsync()
        {
            if (_readsTask == null)
            {
                throw new Exception("The channel has not been started yet.");
            }

            while (true)
            {
                TPackageInfo package = default;
                try
                {
                    package = _packMessageQueue.Take(_cts.Token);
                }
                catch (Exception)
                {
                }

                if (package == null)
                {
                    await HandleClosing();
                    yield break;
                }

                yield return package;
            }
        }

        public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer)
        {
            try
            {
                await this.SendLock.WaitAsync();
                PipeWriter writer = _connection.Transport.Output;
                CheckChannelOpen();
                await writer.WriteAsync(buffer);
                await writer.FlushAsync();
            }
            finally
            {
                this.SendLock.Release();
            }
        }

        public override async ValueTask SendAsync<TPackage>(IPackageEncoder<TPackage> packageEncoder, TPackage package)
        {
            try
            {
                await this.SendLock.WaitAsync();
                PipeWriter writer = _connection.Transport.Output;
                CheckChannelOpen();
                packageEncoder.Encode(writer, package);
                await writer.FlushAsync();
            }
            finally
            {
                this.SendLock.Release();
            }
        }

        public override async ValueTask SendAsync(Action<PipeWriter> write)
        {
            try
            {
                await this.SendLock.WaitAsync();
                PipeWriter writer = _connection.Transport.Output;
                write(writer);
                await writer.FlushAsync();
            }
            finally
            {
                this.SendLock.Release();
            }
        }

        public override async ValueTask CloseAsync(CloseReason closeReason)
        {
            this.CloseReason = closeReason;
            _cts.Cancel();
            await HandleClosing();
        }

        public override async ValueTask DetachAsync()
        {
            _isDetaching = true;
            _cts.Cancel();
            await HandleClosing();
            _isDetaching = false;
        }

        #region 读写数据

        private async Task ReceivePacketAsync()
        {
            PipeReader input = _connection.Transport.Input;
            CancellationTokenSource cts = _cts;

            while (!cts.IsCancellationRequested)
            {
                ReadResult result;

                try
                {
                    result = await input.ReadAsync(cts.Token);
                }
                catch (Exception e)
                {
                    if (!IsIgnorableException(e))
                    {
                        OnError("Failed to read from the pipe", e);
                    }

                    break;
                }

                ReadOnlySequence<byte> buffer = result.Buffer;

                SequencePosition consumed = buffer.Start;
                SequencePosition examined = buffer.End;

                if (result.IsCanceled)
                {
                    break;
                }

                bool completed = result.IsCompleted;

                try
                {
                    if (buffer.Length > 0)
                    {
                        // 设置时间
                        this.LastActiveTime = DateTimeOffset.Now;

                        if (!ReaderBuffer(ref buffer, out consumed, out examined))
                        {
                            completed = true;
                            break;
                        }
                    }

                    if (completed)
                    {
                        break;
                    }
                }
                catch (Exception e)
                {
                    OnError("Protocol error", e);

                    // close the connection if get a protocol error
                    Close();
                    break;
                }
                finally
                {
                    input.AdvanceTo(consumed, examined);
                }
            }

            input.Complete();
        }

        private bool ReaderBuffer(ref ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            consumed = buffer.Start;
            examined = buffer.End;

            long bytesConsumedTotal = 0L;

            int maxPackageLength = this.Options.MaxPackageLength;

            SequenceReader<byte> seqReader = new SequenceReader<byte>(buffer);

            while (true)
            {
                IPipelineFilter<TPackageInfo> currentPipelineFilter = _pipelineFilter;
                bool filterSwitched = false;

                TPackageInfo packageInfo = currentPipelineFilter.Filter(ref seqReader);

                IPipelineFilter<TPackageInfo> nextFilter = currentPipelineFilter.NextFilter;

                if (nextFilter != null)
                {
                    nextFilter.Context = currentPipelineFilter.Context; // pass through the context
                    _pipelineFilter = nextFilter;
                    filterSwitched = true;
                }

                long bytesConsumed = seqReader.Consumed;
                bytesConsumedTotal += bytesConsumed;

                long len = bytesConsumed;

                // nothing has been consumed, need more data
                if (len == 0)
                {
                    len = seqReader.Length;
                }

                if (maxPackageLength > 0 && len > maxPackageLength)
                {
                    OnError($"Package cannot be larger than {maxPackageLength}.");
                    // close the the connection directly
                    Close();
                    return false;
                }

                if (packageInfo == null)
                {
                    // the current pipeline filter needs more data to process
                    if (!filterSwitched)
                    {
                        // set consumed position and then continue to receive...
                        consumed = buffer.GetPosition(bytesConsumedTotal);
                        return true;
                    }

                    // we should reset the previous pipeline filter after switch
                    currentPipelineFilter.Reset();
                }
                else
                {
                    // reset the pipeline filter after we parse one full package
                    currentPipelineFilter.Reset();

                    _packMessageQueue.Add(packageInfo);
                }

                if (seqReader.End) // no more data
                {
                    examined = consumed = buffer.End;
                    return true;
                }

                if (bytesConsumed > 0)
                {
                    seqReader = new SequenceReader<byte>(seqReader.Sequence.Slice(bytesConsumed));
                }
            }
        }

        #endregion

        #region 私有方法

        private async void WaitHandleClosing()
        {
            await HandleClosing();
        }

        private void OnError(string message, Exception e = null)
        {
            if (e != null)
            {
                this.Logger?.LogError(e, message);
            }
            else
            {
                this.Logger?.LogError(message);
            }
        }

        private void CheckChannelOpen()
        {
            if (this.IsClosed)
            {
                throw new Exception("Channel is closed now, send is not allowed.");
            }
        }

        private async ValueTask HandleClosing()
        {
            try
            {
                await _readsTask;
                _packMessageQueue?.Dispose();
                _packMessageQueue = null;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                OnError("Unhandled exception in the method PipeChannel.Run.", e);
            }
            finally
            {
                if (!_isDetaching && !this.IsClosed)
                {
                    try
                    {
                        Close();
                        OnClosed();
                    }
                    catch (Exception exc)
                    {
                        if (!IsIgnorableException(exc))
                        {
                            OnError("Unhandled exception in the method PipeChannel.Close.", exc);
                        }
                    }
                }
            }
        }

        private void Close()
        {
            // 此方法会断开客户端的Tcp连接
            _connection.Transport.Input?.Complete();
            _connection.Transport.Output?.Complete();
        }

        private bool IsIgnorableException(Exception e)
        {
            if (e is ObjectDisposedException or NullReferenceException or OperationCanceledException)
            {
                return true;
            }

            if (e.InnerException != null)
            {
                return IsIgnorableException(e.InnerException);
            }

            return false;
        }

        #endregion
    }
}
