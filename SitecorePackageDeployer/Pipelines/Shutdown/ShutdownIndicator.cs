using Hhogdev.SitecorePackageDeployer.Tasks;
using Sitecore.Pipelines;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hhogdev.SitecorePackageDeployer.Pipelines.Shutdown
{
    /// <summary>
    /// Catches a shutdown and tells the package installer that it must execute its post steps in the initialize pipeline
    /// </summary>
    public class ShutdownIndicator
    {
        public void Process(PipelineArgs args)
        {
            //tell the package installer that Sitecore is shutting down and the package installer must process post steps later
            InstallPackage.ShutdownDetected = true;
        }
    }
}
