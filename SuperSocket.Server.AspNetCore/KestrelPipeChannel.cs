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

        public IPipelineFilter PipelineFilter => this._pipelineFilter;

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
            this._readsTask = this.ReceivePacketAsync();
            this.WaitHandleClosing();
        }

        public override async IAsyncEnumerable<TPackageInfo> RunAsync()
        {
            if (this._readsTask == null)
            {
                throw new Exception("The channel has not been started yet.");
            }

            while (true)
            {
                TPackageInfo package = default;
                try
                {
                    package = this._packMessageQueue.Take(this._cts.Token);
                }
                catch (Exception)
                {
                }

                if (package == null)
                {
                    await this.HandleClosing();
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
                var writer = this._connection.Transport.Output;
                this.CheckChannelOpen();
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
                var writer = this._connection.Transport.Output;
                this.CheckChannelOpen();
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
                var writer = this._connection.Transport.Output;
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
            this._cts.Cancel();
            await this.HandleClosing();
        }

        public override async ValueTask DetachAsync()
        {
            this._isDetaching = true;
            this._cts.Cancel();
            await this.HandleClosing();
            this._isDetaching = false;
        }

        #region 读写数据

        private async Task ReceivePacketAsync()
        {
            var input = this._connection.Transport.Input;
            var cts = this._cts;

            while (!cts.IsCancellationRequested)
            {
                ReadResult result;

                try
                {
                    result = await input.ReadAsync(cts.Token);
                }
                catch (Exception e)
                {
                    if (!this.IsIgnorableException(e))
                    {
                        this.OnError("Failed to read from the pipe", e);
                    }

                    break;
                }

                var buffer = result.Buffer;

                var consumed = buffer.Start;
                var examined = buffer.End;

                if (result.IsCanceled)
                {
                    break;
                }

                var completed = result.IsCompleted;

                try
                {
                    if (buffer.Length > 0)
                    {
                        // 设置时间
                        this.LastActiveTime = DateTimeOffset.Now;

                        if (!this.ReaderBuffer(ref buffer, out consumed, out examined))
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
                    this.OnError("Protocol error", e);

                    // close the connection if get a protocol error
                    this.Close();
                    break;
                }
                finally
                {
                    input.AdvanceTo(consumed, examined);
                }
            }

            input.Complete();

            // EOF
            this._packMessageQueue.Add(default);
        }

        private bool ReaderBuffer(ref ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            consumed = buffer.Start;
            examined = buffer.End;

            var bytesConsumedTotal = 0L;

            var maxPackageLength = this.Options.MaxPackageLength;

            var seqReader = new SequenceReader<byte>(buffer);

            while (true)
            {
                var currentPipelineFilter = this._pipelineFilter;
                var filterSwitched = false;

                var packageInfo = currentPipelineFilter.Filter(ref seqReader);

                var nextFilter = currentPipelineFilter.NextFilter;

                if (nextFilter != null)
                {
                    nextFilter.Context = currentPipelineFilter.Context; // pass through the context
                    this._pipelineFilter = nextFilter;
                    filterSwitched = true;
                }

                var bytesConsumed = seqReader.Consumed;
                bytesConsumedTotal += bytesConsumed;

                var len = bytesConsumed;

                // nothing has been consumed, need more data
                if (len == 0)
                {
                    len = seqReader.Length;
                }

                if (maxPackageLength > 0 && len > maxPackageLength)
                {
                    this.OnError($"Package cannot be larger than {maxPackageLength}.");
                    // close the the connection directly
                    this.Close();
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

                    this._packMessageQueue.Add(packageInfo);
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
            await this.HandleClosing();
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
                await this._readsTask;
                this._packMessageQueue?.Dispose();
                this._packMessageQueue = null;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                this.OnError("Unhandled exception in the method PipeChannel.Run.", e);
            }
            finally
            {
                if (!this._isDetaching && !this.IsClosed)
                {
                    try
                    {
                        this.Close();
                        this.OnClosed();
                    }
                    catch (Exception exc)
                    {
                        if (!this.IsIgnorableException(exc))
                        {
                            this.OnError("Unhandled exception in the method PipeChannel.Close.", exc);
                        }
                    }
                }
            }
        }

        private void Close()
        {
            // 此方法会断开客户端的Tcp连接
            this._connection.Transport.Input?.Complete();
            this._connection.Transport.Output?.Complete();
        }

        private bool IsIgnorableException(Exception e)
        {
            if (e is ObjectDisposedException or NullReferenceException or OperationCanceledException)
            {
                return true;
            }

            if (e.InnerException != null)
            {
                return this.IsIgnorableException(e.InnerException);
            }

            return false;
        }

        #endregion
    }
}
