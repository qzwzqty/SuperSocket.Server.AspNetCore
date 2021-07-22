﻿using System;
using System.Threading.Tasks;
using SuperSocket.Command;

namespace TcpDemo
{
    [Command(Key = "Demo")]
    public class DemoCommand : IAsyncCommand<DemoSession, DemoPackInfo>
    {
        public ValueTask ExecuteAsync(DemoSession session, DemoPackInfo package)
        {
            throw new NotImplementedException();
        }
    }
}
