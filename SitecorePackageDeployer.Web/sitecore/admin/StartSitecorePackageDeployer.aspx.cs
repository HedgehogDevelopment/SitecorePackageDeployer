using Hhogdev.SitecorePackageDeployer.Tasks;
using System;
using System.Threading;

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
                var installer = new InstallPackage();
                installer.Run();
            }
            else
            {
                ThreadPool.QueueUserWorkItem((ctx) =>
                {
                    var installer = new InstallPackage();
                    installer.Run();
                });
            }
                
        }
    }
}