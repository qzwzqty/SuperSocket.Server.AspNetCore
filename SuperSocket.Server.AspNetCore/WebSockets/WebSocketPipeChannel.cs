using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SuperSocket;
using SuperSocket.Channel;
using SuperSocket.ProtoBase;

namespace SuperSocket.Server.AspNetCore.WebSockets
{
    public class WebSocketPipeChannel<TPackageInfo> : ChannelBase<TPackageInfo>, IChannel<TPackageInfo>, IChannel, IPipeChannel
    {
        public Pipe In => throw new NotImplementedException();

        public Pipe Out => throw new NotImplementedException();

        public IPipelineFilter PipelineFilter => this._pipelineFilter;

        protected SemaphoreSlim SendLock { get; } = new SemaphoreSlim(1, 1);

        protected ILogger Logger { get; }

        protected ChannelOptions Options { get; }

        private IPipelineFilter<TPackageInfo> _pipelineFilter;
        private readonly WebSocket _webSocket;
        private readonly CancellationTokenSource _cts = new();
        private bool _isDetaching = false;
        private Task _readsTask;
        private BlockingCollection<TPackageInfo> _packMessageQueue = new();

        public WebSocketPipeChannel(
            IPipelineFilter<TPackageInfo> pipelineFilter,
            ChannelOptions options,
            WebSocket webSocket)
        {
            this._pipelineFilter = pipelineFilter;
            this.Options = options;
            this.Logger = options.Logger;
            this._webSocket = webSocket;
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
                this.CheckChannelOpen();

                await this._webSocket.SendAsync(buffer, WebSocketMessageType.Binary, true, this._cts.Token);
            }
            finally
            {
                this.SendLock.Release();
            }
        }

        public override ValueTask SendAsync<TPackage>(IPackageEncoder<TPackage> packageEncoder, TPackage package)
        {
            throw new NotImplementedException();
        }

        public override ValueTask SendAsync(Action<PipeWriter> write)
        {
            throw new NotImplementedException();
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

        #region 私有方法

        private async Task ReceivePacketAsync()
        {
            while (this._webSocket.State == WebSocketState.Open)
            {
                var buffer = new ArraySegment<byte>(new byte[1024 * 4]);

                WebSocketReceiveResult result;

                try
                {
                    using var ms = new MemoryStream();
                    do
                    {
                        result = await this._webSocket.ReceiveAsync(buffer, this._cts.Token).ConfigureAwait(false);
                        ms.Write(buffer.Array, buffer.Offset, result.Count);
                    }
                    while (!result.EndOfMessage);

                    ms.Seek(0, SeekOrigin.Begin);

                    using (var reader = new StreamReader(ms, Encoding.UTF8))
                    {
                        var a = await reader.ReadToEndAsync();
                    }

                    try
                    {
                        var bytes = ms.ToArray();
                        if (bytes.Length <= 0 || result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        // 设置时间
                        this.LastActiveTime = DateTimeOffset.Now;

                        if (!this.ReaderBuffer(bytes))
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
                    }
                }
                catch (Exception e)
                {
                    if (!this.IsIgnorableException(e))
                    {
                        this.OnError("Failed to read from the pipe", e);
                    }

                    break;
                }
            }

            // EFO
            this._packMessageQueue.Add(default);
        }

        private bool ReaderBuffer(byte[] bytes)
        {
            if (bytes.Length <= 0)
            {
                return false;
            }

            var buffer = new ReadOnlySequence<byte>(bytes);
            var seqReader = new SequenceReader<byte>(buffer);
            var bytesConsumedTotal = 0L;
            var maxPackageLength = this.Options.MaxPackageLength;

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
                    return true;
                }

                if (bytesConsumed > 0)
                {
                    seqReader = new SequenceReader<byte>(seqReader.Sequence.Slice(bytesConsumed));
                }
            }
        }

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
            this._webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                .DoNotAwait();
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
