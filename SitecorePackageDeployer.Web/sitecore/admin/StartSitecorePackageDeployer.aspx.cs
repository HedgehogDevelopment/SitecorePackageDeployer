using System;
using System.Threading;
using System.Web.UI;
using Hhogdev.SitecorePackageDeployer.Tasks;

namespace Hhogdev.SitecorePackageDeployer.Web.sitecore.admin
{
    public partial class StartSitecorePackageDeployer : Page
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            if (Request.QueryString["force"] == "1")
            {
                InstallPackage.ResetInstallState();
            }

            ThreadPool.QueueUserWorkItem(ctx =>
            {
                var installer = new InstallPackage();
                installer.Run();
            });
        }
    }
}