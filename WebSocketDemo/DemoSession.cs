using Microsoft.Extensions.Logging;
using SuperSocket.Server.AspNetCore;

namespace WebSocketDemo
{
    public class DemoSession : KestrelSession
    {
        public DemoSession(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }
    }
}
