using Hhogdev.SitecorePackageDeployer.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Web;
using System.Web.UI;
using System.Web.UI.WebControls;

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

            ThreadPool.QueueUserWorkItem((ctx) =>
            {
                InstallPackage installer = new InstallPackage();
                installer.Run();
            });
        }
    }
}