# Sitecore Package Deployer
Simplifies the automated deployment of Sitecore update packages by automatically deploying update packages that have been copied into a folder on the Sitecore server. This reduces the complexity of your deployments because copying files to a web server is already something the deployment process does.

# Description
The Sitecore Package Deployer module is made up of three components that work together to deploy the Sitecore update package. 

The first component is a scheduled task run the by the Sitecore task agent. This task runs one per minute and looks for packages in a folder specified by the configuration setting **SitecorePackageDeployer.PackageSource**. When an update package is found, it is installed using the Sitecore update API.

The second component is a pipeline step in the shutdown pipeline. This step sets a flag in the package installer task to indicate that Sitecore is shutting down. The installer needs to know this so it doesn't attempt to run the update package post install steps until Sitecore restarts. If Sitecore is shutting down, the installer task creates a file in the **SitecorePackageDeployer.PackageSource** folder indicating that the update package needs to have it's post steps executed

The last component is a pipeline step in the initialize pipeline. This step looks for a file in the **SitecorePackageDeployer.PackageSource** folder that indicates that the post steps for the update package need to run and runs the post steps as Sitecore is starting up.



  
