using Hhogdev.SitecorePackageDeployer.Tasks;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hhogdev.SitecorePackageDeployer.Web.sitecore.admin
{
    public partial class StartSitecorePackageDeployer : System.Web.UI.Page
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            if (Request.QueryString["force"] == "1")
            {
                InstallPackage.ResetInstallState();
            }

            if (Request.QueryString["synchronous"] == "1")
            {
                var task = Task.Factory.StartNew(Runner);
                Task.WaitAll(task);
            }
            else
            {
                ThreadPool.QueueUserWorkItem((ctx) =>
                {
                    Runner();
                });
            }
        }

        private static void Runner()
        {
            var installer = new InstallPackage();
            installer.Run();
        }
    }
}