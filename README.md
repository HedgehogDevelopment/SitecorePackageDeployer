# Sitecore Package Deployer
The Sitecore Package Deployer uses a Sitecore Job to automatically deploy update packages from a file system folder on the Sitecore server. This reduces the complexity of deployments because copying files to a web server is already something the deployment process does.

# Description
There are three components that the Sitecore Package Deployer module uses to install update Packages. These components work together to deploy the Sitecore update package. 

The first component is a scheduled task run the by the Sitecore task agent. This task runs one per minute and looks for packages in a folder specified by the configuration setting **SitecorePackageDeployer.PackageSource**. When an update package is found, it is installed using the Sitecore update API.

The second component is a pipeline step in the shutdown pipeline. This step sets a flag in the package installer task to indicate Sitecore is shutting down. The installer needs to know this so it doesn't attempt to run the update package post install steps until Sitecore restarts. If Sitecore is shutting down, the installer task creates a file in the **SitecorePackageDeployer.PackageSource** folder indicating the update package needs to have it's post steps executed.

The last component is a pipeline step in the initialize pipeline. This step looks for a file in the **SitecorePackageDeployer.PackageSource** folder that indicates the post steps for the update package need to run. If the file is found, the post steps are run as Sitecore is starting up.

# Operation
To use the Sitecore Package Deployer, simply copy your update packages into the folder you have configured in **SitecorePackageDeployer.PackageSource**. I recommend you use a location in the **/data** folder, since it will have all the correct permissions needed to install the package.

If there are changes to the Sitecore configs or binaries during the installation of the package, the IIS AppPool will be recycled. The Sitecore Package Deployer is aware of this and will finish processing the update package when Sitecore restarts. This same process is how the Update Installation Wizard handles these scenarios. 

If Sitecore is restarting, the Sitecore Package Deployer will make an HTTP request to the cms website just before shutting down to attempt to restart the server automatically. In some cases, the url for the server can't be determined, so the url from the setting **SitecorePackageDeployer.RestartUrl** is used instead.


# Config files
One of the most popular questions the TDS support team receives about update packages questions the way Sitecore handles config files. When the update package installer determines that a config file needs to be replaced, the Sitecore installer creates config with a new name and leaves the original file in place. The replacement of the file is done manually by the developer performing the install. This makes it much more difficult to automate the installation process.

To make the deployment fully automatic, the Sitecore Package Deployer finds the new .config file(s), makes a backup of the existing .config file(s) and performs the replacement automatically. This happens before the post steps are run, and will cause the AppPool to recycle. 

# Installing the Sitecore Package Deployer
The update package installer can be downloaded as an update package from [here](https://github.com/HedgehogDevelopment/SitecorePackageDeployer/blob/master/Hhogdev.SitecorePackageDeployer_v1.0.update?raw=true). This is an update package and can be installed using the Update Installation Wizard in Sitecore. Once it is installed, the deploy folder will automatically be created in the default location of **$(dataFolder)\SitecorePackageDeployer**. Simply drop your update packages in that location and they will be automatically installed.

The Sitecore Package Deployer has been tested with the latest version of Sitecore 7.0, 7.1, 7.2, 7.5 and 8.0

#Modifying the Sitecore Package Deployer
If you wish to modify Sitecore Package Deployer, simple fork the repository. You may need to make a few simple modifications depending on your local environment. At Hedgehog, we use an internal NuGet server to distribute the Sitecore assemblies. If you follow a similar practice, you should restore the proper NuGet packages and everything should be pointing to the correct locations.

If you do not use NuGet, you will need to add references to the Sitecore .dll's in the **Hhogdev.SitecorePackageDeployer** project and update the location the TDS Update Package Builder uses to locate the Sitecore .dlls. 

Please make sure to all Sitecore assemblies have the CopyLocal property set to false, so they are not included in the deployment or update package. 
