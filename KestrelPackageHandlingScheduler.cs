using System;
using System.Threading.Tasks;
using SuperSocket.Channel;

namespace SuperSocket.Server.AspNetCore
{
    public class KestrelPackageHandlingScheduler<TPackageInfo> : IPackageHandlingScheduler<TPackageInfo>
    {
        public IPackageHandler<TPackageInfo> PackageHandler { get; private set; }

        public Func<IAppSession, PackageHandlingException<TPackageInfo>, ValueTask<bool>> ErrorHandler { get; private set; }

        public async ValueTask HandlePackage(IAppSession session, TPackageInfo package)
        {
            IPackageHandler<TPackageInfo> packageHandler = this.PackageHandler;
            Func<IAppSession, PackageHandlingException<TPackageInfo>, ValueTask<bool>> errorHandler = this.ErrorHandler;

            try
            {
                if (packageHandler != null)
                {
                    await packageHandler.Handle(session, package);
                }
            }
            catch (Exception e)
            {
                bool toClose = await errorHandler(session, new PackageHandlingException<TPackageInfo>($"Session {session.SessionID} got an error when handle a package.", package, e));

                if (toClose)
                {
                    session.CloseAsync(CloseReason.ApplicationError).DoNotAwait();
                }
            }
        }

        public virtual void Initialize(IPackageHandler<TPackageInfo> packageHandler, Func<IAppSession, PackageHandlingException<TPackageInfo>, ValueTask<bool>> errorHandler)
        {
            this.PackageHandler = packageHandler;
            this.ErrorHandler = errorHandler;
        }
    }
}
