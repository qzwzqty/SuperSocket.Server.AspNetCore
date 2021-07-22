using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SuperSocket.Channel;
using SuperSocket.ProtoBase;

namespace SuperSocket.Server.AspNetCore
{
    public class KestrelSession : IAppSession, ILogger, ILoggerAccessor
    {
        public KestrelSession(ILoggerFactory loggerFactory)
        {
            this.Logger = loggerFactory.CreateLogger("IAppSession");
        }

        #region IAppSession

        #region 方法

        public ValueTask SendAsync(ReadOnlyMemory<byte> data)
        {
            return this.Channel.SendAsync(data);
        }

        public ValueTask SendAsync<TPackage>(IPackageEncoder<TPackage> packageEncoder, TPackage package)
        {
            return this.Channel.SendAsync(packageEncoder, package);
        }

        public virtual async ValueTask CloseAsync(CloseReason reason)
        {
            IChannel channel = this.Channel;

            if (channel == null)
            {
                return;
            }

            try
            {
                await channel.CloseAsync(reason);
            }
            catch
            {
            }
        }

        public virtual async ValueTask CloseAsync()
        {
            await CloseAsync(CloseReason.LocalClosing);
        }

        void IAppSession.Initialize(IServerInfo server, IChannel channel)
        {
            this.StartTime = DateTimeOffset.Now;
            this.Channel = channel;
            this.State = SessionState.Initialized;
        }

        void IAppSession.Reset()
        {
            ClearEvent(ref Connected);
            ClearEvent(ref Closed);
            _items?.Clear();
            this.State = SessionState.None;
            this.Channel = null;
            this.DataContext = null;
            this.StartTime = default;

            Reset();
        }

        protected virtual void Reset()
        {

        }

        protected virtual ValueTask OnSessionClosedAsync(CloseEventArgs e)
        {
            return new ValueTask();
        }

        internal async ValueTask FireSessionClosedAsync(CloseEventArgs e)
        {
            this.State = SessionState.Closed;

            await OnSessionClosedAsync(e);

            AsyncEventHandler<CloseEventArgs> closeEventHandler = Closed;

            if (closeEventHandler == null)
            {
                return;
            }

            await closeEventHandler.Invoke(this, e);
        }

        protected virtual ValueTask OnSessionConnectedAsync()
        {
            return new ValueTask();
        }

        internal async ValueTask FireSessionConnectedAsync()
        {
            this.State = SessionState.Connected;

            await OnSessionConnectedAsync();

            AsyncEventHandler connectedEventHandler = Connected;

            if (connectedEventHandler == null)
            {
                return;
            }

            await connectedEventHandler.Invoke(this, EventArgs.Empty);
        }

        #endregion

        #region 属性

        /// <summary>
        /// 必须初始化
        /// </summary>
        public string SessionID { get; set; }

        /// <summary>
        /// 初始连接时间
        /// </summary>
        public DateTimeOffset StartTime { get; private set; }

        /// <summary>
        /// 最新接收数据的时间
        /// </summary>
        public DateTimeOffset LastActiveTime => this.Channel?.LastActiveTime ?? DateTimeOffset.MinValue;

        /// <summary>
        /// 通道
        /// </summary>
        public IChannel Channel { get; private set; }

        public EndPoint RemoteEndPoint => this.Channel?.RemoteEndPoint;

        public EndPoint LocalEndPoint => this.Channel?.LocalEndPoint;

        /// <summary>
        /// 服务（未实现）
        /// </summary>
        public IServerInfo Server => throw new NotImplementedException();

        public object DataContext { get; set; }

        private Dictionary<object, object> _items;

        public object this[object name]
        {
            get
            {
                Dictionary<object, object> items = _items;

                return items == null ? null : items.TryGetValue(name, out object value) ? value : null;
            }

            set
            {
                lock (this)
                {
                    Dictionary<object, object> items = _items;

                    if (items == null)
                    {
                        items = _items = new Dictionary<object, object>();
                    }

                    items[name] = value;
                }
            }
        }

        /// <summary>
        /// 当前状态
        /// </summary>
        public SessionState State { get; private set; } = SessionState.None;

        #endregion

        #region 事件

        public event AsyncEventHandler Connected;

        public event AsyncEventHandler<CloseEventArgs> Closed;

        #endregion

        #region 私有方法

        private static void ClearEvent<TEventHandler>(ref TEventHandler sessionEvent)
            where TEventHandler : Delegate
        {
            if (sessionEvent == null)
            {
                return;
            }

            foreach (Delegate handler in sessionEvent.GetInvocationList())
            {
                sessionEvent = Delegate.Remove(sessionEvent, handler) as TEventHandler;
            }
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            throw new NotImplementedException();
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            throw new NotImplementedException();
        }

        #endregion

        #endregion

        #region Logger

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            this.Logger.Log(logLevel, eventId, state, exception, (s, e) =>
            {
                return $"Session[{this.SessionID}]: {formatter(s, e)}";
            });
        }

        bool ILogger.IsEnabled(LogLevel logLevel)
        {
            return this.Logger.IsEnabled(logLevel);
        }

        IDisposable ILogger.BeginScope<TState>(TState state)
        {
            return this.Logger.BeginScope<TState>(state);
        }

        public ILogger Logger { get; }

        #endregion
    }
}
