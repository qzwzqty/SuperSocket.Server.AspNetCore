using SuperSocket.ProtoBase;

namespace WebSocketDemo
{
    public class DemoPackInfo : IKeyedPackageInfo<string>
    {
        public string Key { get; set; }

        public string Message { get; set; }
    }
}
