# Sitecore Package Deployer
Simplifies the automated deployment of Sitecore update packages by automatically deploying update packages that have been copied into a folder on the Sitecore server. This reduces the complexity of your deployments because copying files to a web server is already something the deployment process does.

# Description
The Sitecore Package Deployer module is made up of three components that work together to deploy the Sitecore update package. 

The first component is a scheduled task run the by the Sitecore task agent. This task runs one per minute and looks for packages in a folder specified by the configuration setting **SitecorePackageDeployer.PackageSource**. When an update package is found, it is installed using the Sitecore update API.

The second component is a pipeline step in the shutdown pipeline. This step sets a flag in the package installer task to indicate that Sitecore is shutting down. The installer needs to know this so it doesn't attempt to run the update package post install steps until Sitecore restarts. If Sitecore is shutting down, the installer task creates a file in the **SitecorePackageDeployer.PackageSource** folder indicating that the update package needs to have it's post steps executed

The last component is a pipeline step in the initialize pipeline. This step looks for a file in the **SitecorePackageDeployer.PackageSource** folder that indicates that the post steps for the update package need to run and runs the post steps as Sitecore is starting up.

# Operation
To use the Sitecore Package Deployer, simply copy your update packages into the folder you have configured in **SitecorePackageDeployer.PackageSource**. I recommend you use a location in the /data folder, since it will have all the correct permissions. 

If there are changes to the Sitecore configs or binaries, the IIS AppPool will be recycled. The Sitecore Package Deployer is aware of this and will finish processing the update when Sitecore restarts. This is how the Update Installation Wizard handles these scenarios. If Sitecore is restarting, the Sitecore Package Deployer will make an http request to the root of the website just before shutting down. This request will attempt to restart the server automatically. If the server url can't be determined, it may be configured using the setting **SitecorePackageDeployer.RestartUrl**.

# Config files
One of the most popular questions the TDS support team receives about update packages is the way Sitecore handles config files. When a config file needs to be replaced, the Sitecore installer creates config with a new name and leaves the replacement of the file up to the developer performing the install. This makes it much more difficult to automate the installation process.

The Sitecore Package Deployer finds the new .config file(s), makes a backup of the existing .config file(s) and performs the replacement. All this takes place in the method **FindAndUpdateChangedConfigs**. 

# Installing the Sitecore Package Deployer
Download and install the SitecorePackageDeployer update package and install it in Sitecore. The deploy folder will automatically be created in the default location of $(dataFolder)\SitecorePackageDeployer. The Sitecore Package Deployer has been tested with the latest version of Sitecore 7.0, 7.1, 7.2, 7.5 and 8.0
