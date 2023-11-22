﻿using AzureNamingTool.Models;
using AzureNamingTool.Services;
using System.Collections;
using System.Data.SqlTypes;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.Caching;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureNamingTool.Helpers
{
    public class ConfigurationHelper
    {
        public static SiteConfiguration GetConfigurationData()
        {
            var config = new ConfigurationBuilder()
                    .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                    .AddJsonFile("settings/appsettings.json")
                    .Build()
                    .Get<SiteConfiguration>();
            return config!;
        }

        public static string GetAppSetting(string key, bool decrypt = false)
        {
            string value = String.Empty;
            try
            {
                // Check if the data is cached
                var items = CacheHelper.GetCacheObject(key);
                if (items == null)
                {
                    var config = GetConfigurationData();

                    // Check if the app setting is already set
                    if (GeneralHelper.IsNotNull(config.GetType().GetProperty(key)))
                    {
                        value = config!.GetType()!.GetProperty(key)!.GetValue(config, null)!.ToString()!;

                        // Verify the value is encrypted, and should be decrypted
                        if ((decrypt) && (!String.IsNullOrEmpty(value)) && (GeneralHelper.IsBase64Encoded(value)))
                        {
                            value = GeneralHelper.DecryptString(value, config.SALTKey!);
                        }

                        // Set the result to cache
                        CacheHelper.SetCacheObject(key, value!);
                    }
                    else
                    {
                        // Create a new configuration object and get the default for the property
                        SiteConfiguration newconfig = new();
                        value = newconfig!.GetType()!.GetProperty(key)!.GetValue(newconfig, null)!.ToString()!;

                        // Set the result to the app settings
                        SetAppSetting(key, value, decrypt);

                        // Set the result to cache
                        CacheHelper.SetCacheObject(key, value);
                    }
                }
                else
                {
                    value = items.ToString()!;
                }
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
            return value;
        }

        public static async void SetAppSetting(string key, string value, bool encrypt = false)
        {
            try
            {
                var config = GetConfigurationData();
                string valueoriginal = value;
                if (encrypt)
                {
                    value = GeneralHelper.EncryptString(value, config.SALTKey!);
                }
                Type? type = config.GetType();
                System.Reflection.PropertyInfo propertyInfo = type.GetProperty(key)!;
                propertyInfo.SetValue(config, value, null);
                await UpdateSettings(config);
                // Save the original value to the cache
                CacheHelper.SetCacheObject(key, valueoriginal);
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
        }

        public static async void VerifyConfiguration(StateContainer state)
        {
            try
            {
                // Get all the files in the repository folder
                DirectoryInfo repositoryDir = new("repository");
                foreach (FileInfo file in repositoryDir.GetFiles())
                {
                    // Check if the file exists in the settings folder
                    if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings/" + file.Name)))
                    {
                        // Copy the repository file to the settings folder
                        file.CopyTo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings/" + file.Name));
                    }
                }

                // Migrate old data to new files, if needed
                // Check if the admin log file exists in the settings folder and the adminmessages does not
                if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings/adminlog.json")) && !File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings/adminlogmessages.json")))
                {
                    // Migrate the data
                    await FileSystemHelper.MigrateDataToFile("adminlog.json", "settings/", "adminlogmessages.json", "settings/", true);
                }

                // Sync cnfiguration data
                if (!state.ConfigurationDataSynced)
                {
                    await SyncConfigurationData("ResourceComponent");
                    state.SetConfigurationDataSynced(true);
                }
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
        }

        public static async void VerifySecurity(StateContainer state)
        {
            try
            {
                var config = GetConfigurationData();
                if (!state.Verified)
                {
                    if (String.IsNullOrEmpty(config.SALTKey))
                    {
                        // Create a new SALT key 
                        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
                        Random random = new();
                        var salt = new string(Enumerable.Repeat(chars, 16)
                            .Select(s => s[random.Next(s.Length)]).ToArray());

                        config.SALTKey = salt.ToString();
                        config.APIKey = GeneralHelper.EncryptString(config.APIKey!, salt.ToString());
                        config.ReadOnlyAPIKey = GeneralHelper.EncryptString(config.ReadOnlyAPIKey!, salt.ToString());

                        if (!String.IsNullOrEmpty(config.AdminPassword))
                        {
                            config.AdminPassword = GeneralHelper.EncryptString(config.AdminPassword, config.SALTKey.ToString());
                            state.Password = true;
                        }
                        else
                        {
                            state.Password = false;
                        }
                    }

                    if (!String.IsNullOrEmpty(config.AdminPassword))
                    {
                        state.Password = true;
                    }
                    else
                    {
                        state.Password = false;
                    }
                    await UpdateSettings(config);

                }
                state.SetVerified(true);

                // Set the site theme
                state.SetAppTheme(config.AppTheme!);
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
        }

        public static async Task<bool> VerifyConnectivity()
        {
            bool pingsuccessful = false;
            bool result = false;
            try
            {
                // Check if the data is cached
                var items = CacheHelper.GetCacheObject("isconnected");
                if (items == null)
                {
                    // Check if the connectivity check is enabled
                    if (Convert.ToBoolean(ConfigurationHelper.GetAppSetting("ConnectivityCheckEnabled")))
                    {
                        // Atempt to ping a url first
                        Ping ping = new();
                        String host = "github.com";
                        byte[] buffer = new byte[32];
                        int timeout = 1000;
                        PingOptions pingOptions = new();
                        try
                        {
                            PingReply reply = ping.Send(host, timeout, buffer, pingOptions);
                            if (reply.Status == IPStatus.Success)
                            {
                                pingsuccessful = true;
                                result = true;
                            }
                        }
                        catch (Exception)
                        {
                            // Catch this exception but continue to try a web request instead
                        }

                        // If ping is not successful, attempt to download a file
                        if (!pingsuccessful)
                        {
                            // Atempt to download a file
                            var client = new HttpClient(new HttpClientHandler() { UseDefaultCredentials = true });
                            using var response = await client.GetAsync("https://github.com/mspnp/AzureNamingTool/blob/main/src/connectiontest.png");
                            if (response.StatusCode == HttpStatusCode.OK)
                            {
                                result = true;
                            }
                            else
                            {
                                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = "Connectivity Check Failed:" + response.ReasonPhrase });
                            }
                        }
                    }
                    else
                    {
                        result = true;
                    }
                }
                else
                {
                    result = (bool)items;
                }
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = "There was a problem verifying connectivty. Error: " + ex.Message });
            }

            // Set the result to cache
            CacheHelper.SetCacheObject("isconnected", result);
            return result;
        }

        public async static Task<List<T>?> GetList<T>()
        {
            var items = new List<T>();
            try
            {
                // Check if the data is cached
                String data = (string)CacheHelper.GetCacheObject(typeof(T).Name)!;
                // Load the data from the file system.
                if (String.IsNullOrEmpty(data))
                {
                    data = typeof(T).Name switch
                    {
                        nameof(ResourceComponent) => await FileSystemHelper.ReadFile("resourcecomponents.json"),
                        nameof(ResourceEnvironment) => await FileSystemHelper.ReadFile("resourceenvironments.json"),
                        nameof(Models.ResourceLocation) => await FileSystemHelper.ReadFile("resourcelocations.json"),
                        nameof(ResourceOrg) => await FileSystemHelper.ReadFile("resourceorgs.json"),
                        nameof(ResourceProjAppSvc) => await FileSystemHelper.ReadFile("resourceprojappsvcs.json"),
                        nameof(Models.ResourceType) => await FileSystemHelper.ReadFile("resourcetypes.json"),
                        nameof(ResourceUnitDept) => await FileSystemHelper.ReadFile("resourceunitdepts.json"),
                        nameof(ResourceFunction) => await FileSystemHelper.ReadFile("resourcefunctions.json"),
                        nameof(ResourceDelimiter) => await FileSystemHelper.ReadFile("resourcedelimiters.json"),
                        nameof(CustomComponent) => await FileSystemHelper.ReadFile("customcomponents.json"),
                        nameof(AdminLogMessage) => await FileSystemHelper.ReadFile("adminlogmessages.json"),
                        nameof(GeneratedName) => await FileSystemHelper.ReadFile("generatednames.json"),
                        nameof(AdminUser) => await FileSystemHelper.ReadFile("adminusers.json"),
                        _ => "[]",
                    };
                    CacheHelper.SetCacheObject(typeof(T).Name, data);
                }

                if (data != "[]")
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        PropertyNameCaseInsensitive = true
                    };

                    items = JsonSerializer.Deserialize<List<T>>(data, options);
                }
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
            return items;
        }

        public async static Task WriteList<T>(List<T> items)
        {
            try
            {
                switch (typeof(T).Name)
                {
                    case nameof(ResourceComponent):
                        await FileSystemHelper.WriteConfiguation(items, "resourcecomponents.json");
                        break;
                    case nameof(ResourceEnvironment):
                        await FileSystemHelper.WriteConfiguation(items, "resourceenvironments.json");
                        break;
                    case nameof(Models.ResourceLocation):
                        await FileSystemHelper.WriteConfiguation(items, "resourcelocations.json");
                        break;
                    case nameof(ResourceOrg):
                        await FileSystemHelper.WriteConfiguation(items, "resourceorgs.json");
                        break;
                    case nameof(ResourceProjAppSvc):
                        await FileSystemHelper.WriteConfiguation(items, "resourceprojappsvcs.json");
                        break;
                    case nameof(Models.ResourceType):
                        await FileSystemHelper.WriteConfiguation(items, "resourcetypes.json");
                        break;
                    case nameof(ResourceUnitDept):
                        await FileSystemHelper.WriteConfiguation(items, "resourceunitdepts.json");
                        break;
                    case nameof(ResourceFunction):
                        await FileSystemHelper.WriteConfiguation(items, "resourcefunctions.json");
                        break;
                    case nameof(ResourceDelimiter):
                        await FileSystemHelper.WriteConfiguation(items, "resourcedelimiters.json");
                        break;
                    case nameof(CustomComponent):
                        await FileSystemHelper.WriteConfiguation(items, "customcomponents.json");
                        break;
                    case nameof(AdminLogMessage):
                        await FileSystemHelper.WriteConfiguation(items, "adminlogmessages.json");
                        break;
                    case nameof(GeneratedName):
                        await FileSystemHelper.WriteConfiguation(items, "generatednames.json");
                        break;
                    case nameof(AdminUser):
                        await FileSystemHelper.WriteConfiguation(items, "adminusers.json");
                        break;
                    default:
                        break;
                }

                String data = String.Empty;
                data = typeof(T).Name switch
                {
                    nameof(ResourceComponent) => await FileSystemHelper.ReadFile("resourcecomponents.json"),
                    nameof(ResourceEnvironment) => await FileSystemHelper.ReadFile("resourceenvironments.json"),
                    nameof(Models.ResourceLocation) => await FileSystemHelper.ReadFile("resourcelocations.json"),
                    nameof(ResourceOrg) => await FileSystemHelper.ReadFile("resourceorgs.json"),
                    nameof(ResourceProjAppSvc) => await FileSystemHelper.ReadFile("resourceprojappsvcs.json"),
                    nameof(Models.ResourceType) => await FileSystemHelper.ReadFile("resourcetypes.json"),
                    nameof(ResourceUnitDept) => await FileSystemHelper.ReadFile("resourceunitdepts.json"),
                    nameof(ResourceFunction) => await FileSystemHelper.ReadFile("resourcefunctions.json"),
                    nameof(ResourceDelimiter) => await FileSystemHelper.ReadFile("resourcedelimiters.json"),
                    nameof(CustomComponent) => await FileSystemHelper.ReadFile("customcomponents.json"),
                    nameof(AdminLogMessage) => await FileSystemHelper.ReadFile("adminlogmessages.json"),
                    nameof(GeneratedName) => await FileSystemHelper.ReadFile("generatednames.json"),
                    nameof(AdminUser) => await FileSystemHelper.ReadFile("adminusers.json"),
                    _ => "[]",
                };

                // Update the cache with the latest data
                CacheHelper.SetCacheObject(typeof(T).Name, data);
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
        }

        public static async Task UpdateSettings(SiteConfiguration config)
        {
            // Clear the cache
            ObjectCache memoryCache = MemoryCache.Default;
            List<string> cacheKeys = memoryCache.Select(kvp => kvp.Key).ToList();
            foreach (string cacheKey in cacheKeys)
            {
                memoryCache.Remove(cacheKey);
            }
            var jsonWriteOptions = new JsonSerializerOptions()
            {
                WriteIndented = true
            };
            jsonWriteOptions.Converters.Add(new JsonStringEnumConverter());

            var newJson = JsonSerializer.Serialize(config, jsonWriteOptions);

            var appSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings/appsettings.json");
            await FileSystemHelper.WriteFile("appsettings.json", newJson);
        }

        public static async Task<string> GetOfficalConfigurationFileVersionData()
        {
            string versiondata = String.Empty;
            try
            {
                versiondata = await GeneralHelper.DownloadString("https://raw.githubusercontent.com/mspnp/AzureNamingTool/main/src/configurationfileversions.json");
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
            return versiondata;
        }

        public static async Task<string> GetCurrentConfigFileVersionData()
        {
            string versiondatajson = String.Empty;
            try
            {
                versiondatajson = await FileSystemHelper.ReadFile("configurationfileversions.json");
                // Check if the user has any version data. This value will be '[]' if not.
                if (versiondatajson == "[]")
                {
                    // Create new version data with default values in /settings file
                    ConfigurationFileVersionData? versiondata = new();
                    await FileSystemHelper.WriteFile("configurationfileversions.json", JsonSerializer.Serialize(versiondata), "settings/");
                    versiondatajson = JsonSerializer.Serialize(versiondata);
                }
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
            return versiondatajson;
        }

        public static async Task<List<string>> VerifyConfigurationFileVersionData()
        {
            List<string> versiondata = new();
            try
            {
                // Get the official version from GitHub
                ConfigurationFileVersionData? officialversiondata = new();
                var officialdatajson = await GetOfficalConfigurationFileVersionData();

                // Get the current version
                ConfigurationFileVersionData? currentversiondata = new();
                var currentdatajson = await GetCurrentConfigFileVersionData();

                // Determine if the version data is different
                if ((GeneralHelper.IsNotNull(officialdatajson)) && (GeneralHelper.IsNotNull(currentdatajson)))
                {
                    officialversiondata = JsonSerializer.Deserialize<ConfigurationFileVersionData>(officialdatajson);
                    currentversiondata = JsonSerializer.Deserialize<ConfigurationFileVersionData>(currentdatajson);

                    if ((GeneralHelper.IsNotNull(officialversiondata)) && (GeneralHelper.IsNotNull(currentversiondata)))
                    {
                        // Compare the versions
                        // Resource Types
                        if (officialversiondata.resourcetypes != currentversiondata.resourcetypes)
                        {
                            versiondata.Add("<h5>Resource Types</h5><hr /><div>The Resource Types Configuration is out of date!<br /><br />It is recommended that you refresh your resource types to the latest configuration.<br /><br /><span class=\"fw-bold\">To Refresh:</span><ul><li>Expand the <span class=\"fw-bold\">Types</span> section</li><li>Expand the <span class=\"fw-bold\">Configuration</span> section</li><li>Select the <span class=\"fw-bold\">Refresh</span> option</li></ul></div><br />");
                        }

                        // Resource Locations
                        if (officialversiondata.resourcelocations != currentversiondata.resourcelocations)
                        {
                            versiondata.Add("<h5>Resource Locations</h5><hr /><div>The Resource Locations Configuration is out of date!<br /><br />It is recommended that you refresh your resource locations to the latest configuration.<br /><br /><span class=\"fw-bold\">To Refresh:</span><ul><li>Expand the <span class=\"fw-bold\">Locations</span> section</li><li>Expand the <span class=\"fw-bold\">Configuration</span> section</li><li>Select the <span class=\"fw-bold\">Refresh</span> option</li></ul></div><br />");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
            return versiondata;
        }

        public static async Task UpdateConfigurationFileVersion(string fileName)
        {
            if (await VerifyConnectivity())
            {
                try
                {
                    // Get the official version from GitHub
                    ConfigurationFileVersionData? officialversiondata = new();
                    var officialdatajson = await GetOfficalConfigurationFileVersionData();

                    // Get the current version
                    ConfigurationFileVersionData? currentversiondata = new();
                    var currentdatajson = await GetCurrentConfigFileVersionData();

                    // Determine if the version data is different
                    if ((GeneralHelper.IsNotNull(officialdatajson)) && (GeneralHelper.IsNotNull(currentdatajson)))
                    {
                        officialversiondata = JsonSerializer.Deserialize<ConfigurationFileVersionData>(officialdatajson);
                        currentversiondata = JsonSerializer.Deserialize<ConfigurationFileVersionData>(currentdatajson);

                        if ((GeneralHelper.IsNotNull(officialversiondata)) && (GeneralHelper.IsNotNull(currentversiondata)))
                        {
                            switch (fileName)
                            {
                                case "resourcetypes":
                                    currentversiondata.resourcetypes = officialversiondata.resourcetypes;
                                    break;
                                case "resourcelocations":
                                    currentversiondata.resourcelocations = officialversiondata.resourcelocations;
                                    break;
                            }
                            //  Update the current configuration file version data
                            await FileSystemHelper.WriteFile("configurationfileversions.json", JsonSerializer.Serialize(currentversiondata), "settings/");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
                }
            }
        }

        public static bool ResetSiteConfiguration()
        {
            bool result = false;
            try
            {
                // Get all the files in the repository folder
                DirectoryInfo repositoryDir = new("repository");
                // Filter out the appsettings.json to retain admin credentials
                string[] protectedfilenames = { "adminusers.json", "appsettings.json" };
                foreach (FileInfo file in repositoryDir.GetFiles())
                {
                    //Only copy non-admin files
                    if (!protectedfilenames.Contains(file.Name.ToLower()))
                    {
                        // Copy the repository file to the settings folder
                        file.CopyTo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings/" + file.Name), true);
                    }
                }

                // Clear the cache
                ObjectCache memoryCache = MemoryCache.Default;
                List<string> cacheKeys = memoryCache.Select(kvp => kvp.Key).ToList();
                foreach (string cacheKey in cacheKeys)
                {
                    memoryCache.Remove(cacheKey);
                }
                result = true;
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
            return result;
        }

        public static void ResetState(StateContainer state)
        {
            state.SetVerified(false);
            state.SetAdmin(false);
            state.SetPassword(false);
            state.SetAppTheme("bg-default text-dark");
        }

        public static string GetAssemblyVersion()
        {
            string result = String.Empty;
            try
            {
                string data = (string)CacheHelper.GetCacheObject("assemblyversion")!;
                if (String.IsNullOrEmpty(data))
                {
                    Version version = Assembly.GetExecutingAssembly().GetName().Version!;
                    result = version.Major + "." + version.Minor + "." + version.Revision;
                    CacheHelper.SetCacheObject("assemblyversion", result);
                }
                else
                {
                    result = data;
                }
                return result;
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
            return result;
        }

        public static async Task<string?> GetToolVersion()
        {
            try
            {
                string versiondata = String.Empty;
                versiondata = await GetProgramSetting("toolVersion");
                return versiondata;

            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
                return null;
            }
        }

        public static async Task<string> GetVersionAlert(bool forceDisplay = false)
        {
            string alert = "";
            try
            {
                VersionAlert versionalert = new();
                bool dismissed = false;
                string appversion = GetAssemblyVersion();

                // Check if version alert has been dismissed
                var dismissedalerts = GetAppSetting("DismissedAlerts").Split(',');
                if (GeneralHelper.IsNotNull(dismissedalerts))
                {
                    if (dismissedalerts.Contains(appversion))
                    {
                        dismissed = true;
                    }
                }

                if ((!dismissed) || (forceDisplay))
                {
                    // Check if the data is cached
                    var cacheddata = CacheHelper.GetCacheObject("versionalert-" + appversion);
                    if (cacheddata == null)
                    {
                        // Get the alert 
                        List<VersionAlert> versionAlerts = new();
                        string data = await FileSystemHelper.ReadFile("versionalerts.json", "");
                        if (GeneralHelper.IsNotNull(data))
                        {
                            var items = new List<VersionAlert>();
                            var options = new JsonSerializerOptions
                            {
                                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                                PropertyNameCaseInsensitive = true
                            };
                            items = JsonSerializer.Deserialize<List<VersionAlert>>(data, options)!.ToList();
                            versionalert = items.Where(x => x.Version == appversion).FirstOrDefault()!;

                            if (GeneralHelper.IsNotNull(versionalert))
                            {
                                alert = versionalert.Alert;
                                // Set the result to cache
                                CacheHelper.SetCacheObject("versionalert-" + appversion, versionalert.Alert);
                            }
                        }
                    }
                    else
                    {
                        alert = (string)cacheddata;
                    }
                }
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
            return alert;
        }

        public static void DismissVersionAlert()
        {
            try
            {
                string appversion = GetAssemblyVersion();
                List<string> dismissedalerts = new(GetAppSetting("DismissedAlerts").Split(','));
                if (!dismissedalerts.Contains(appversion))
                {
                    if (String.IsNullOrEmpty(string.Join(",", dismissedalerts)))
                    {
                        dismissedalerts.Clear();
                    }
                    dismissedalerts.Add(appversion);
                }
                SetAppSetting("DismissedAlerts", string.Join(",", dismissedalerts));
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
        }

        public static async Task<bool> PostToGenerationWebhook(string URL, GeneratedName generatedName)
        {
            bool result = false;
            try
            {
                HttpClient httpClient = new()
                {
                    BaseAddress = new Uri(URL)
                };
                HttpResponseMessage response = await httpClient.PostAsJsonAsync("", generatedName);
                if (response.IsSuccessStatusCode)
                {
                    result = true;
                    AdminLogService.PostItem(new AdminLogMessage() { Title = "INFORMATION", Message = "Generated Name (" + generatedName.ResourceName + ") successfully posted to webhook!" });
                }
                else
                {
                    AdminLogService.PostItem(new AdminLogMessage() { Title = "INFORMATION", Message = "Generated Name (" + generatedName.ResourceName + ") not successfully posted to webhook! " + response.ReasonPhrase });
                }
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "INFORMATION", Message = "Generated Name (" + generatedName.ResourceName + ") not successfully posted to webhook! " + ex.Message });
            }
            return result;
        }
        public static async Task<string> GetProgramSetting(string programSetting)
        {
            string result = String.Empty;
            try
            {
                string data = (string)CacheHelper.GetCacheObject(programSetting)!;
                if (String.IsNullOrEmpty(data))
                {
                    var response = await GeneralHelper.DownloadString("https://raw.githubusercontent.com/mspnp/AzureNamingTool/main/src/programsettings.json");
                    var setting = JsonDocument.Parse(response);
                    result = setting.RootElement.GetProperty(programSetting).ToString();
                    CacheHelper.SetCacheObject(programSetting, result);
                }
                else
                {
                    result = data;
                }
                return result;
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
            return result;
        }

        /// <summary>
        /// This function is used to sync default configuration data with the user's local version
        /// </summary>
        /// <param name="type">string - Type of configuration data to sync</param>
        public static async Task SyncConfigurationData(string type)
        {
            try
            {
                bool update = false;

                switch (type)
                {
                    case "ResourceComponent":
                        // Get all the existing components
                        List<ResourceComponent> currentComponents = new();
                        ServiceResponse serviceResponse = new();
                        serviceResponse = await ResourceComponentService.GetItems(true);
                        if (serviceResponse.Success)
                        {
                            if (GeneralHelper.IsNotNull(serviceResponse))
                            {
                                currentComponents = serviceResponse.ResponseObject!;
                                // Get the default component data
                                List<ResourceComponent> defaultComponents = new();
                                string data = await FileSystemHelper.ReadFile("resourcecomponents.json", "repository/");
                                if (!String.IsNullOrEmpty(data))
                                {
                                    var options = new JsonSerializerOptions
                                    {
                                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                                        PropertyNameCaseInsensitive = true
                                    };

                                    if (!String.IsNullOrEmpty(data))
                                    {
                                        defaultComponents = JsonSerializer.Deserialize<List<ResourceComponent>>(data, options)!;
                                    }

                                    // Loop over the existing components to verify the data is complete
                                    foreach (ResourceComponent currentComponent in currentComponents)
                                    {
                                        // Create a new component for any updates
                                        ResourceComponent newComponent = currentComponent;
                                        // Get the matching default component for the current component
                                        ResourceComponent? defaultcomponent = defaultComponents.Find(x => x.Name == currentComponent.Name);
                                        // Check the data to see if it's been configured
                                        if (String.IsNullOrEmpty(currentComponent.MinLength))
                                        {
                                            if (GeneralHelper.IsNotNull(defaultcomponent))
                                            {
                                                newComponent.MinLength = defaultcomponent.MinLength;
                                            }
                                            else
                                            {
                                                newComponent.MinLength = "1";
                                            }
                                            update = true;
                                        }

                                        // Check the data to see if it's been configured
                                        if (String.IsNullOrEmpty(currentComponent.MaxLength))
                                        {
                                            if (GeneralHelper.IsNotNull(defaultcomponent))
                                            {
                                                newComponent.MaxLength = defaultcomponent.MaxLength;
                                            }
                                            else
                                            {
                                                newComponent.MaxLength = "10";
                                            }
                                            update = true;
                                        }
                                        if (update)
                                        {
                                            await ResourceComponentService.PostItem(newComponent);
                                        }
                                    }
                                }
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
        }

        public static List<KeyValuePair<string,string>> GetEnvironmentVariables()
        {
            List<KeyValuePair<string, string>> result = new();
            try
            {
                var entries = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                              .Select(x => KeyValuePair.Create((string)x.Key, (string)x.Value!));
                var sortedEntries = entries.OrderBy(x => x.Key);
                result = sortedEntries.ToList<KeyValuePair<string, string>>();
                return result;
            }
            catch (Exception ex)
            {
                AdminLogService.PostItem(new AdminLogMessage() { Title = "ERROR", Message = ex.Message });
            }
            return result;
        }


        public static async Task<bool> CheckIfGeneratedNameExists(string name)
        {
            bool nameexists = false;
            // Check if the name already exists
            ServiceResponse serviceResponse = new();
            serviceResponse = await GeneratedNamesService.GetItems();
            if (serviceResponse.Success)
            {
                if (GeneralHelper.IsNotNull(serviceResponse.ResponseObject))
                {
                    var names = (List<GeneratedName>)serviceResponse.ResponseObject!;
                    if (GeneralHelper.IsNotNull(names))
                    {
                        if (names.Where(x => x.ResourceName == name).Any())
                        {
                            nameexists = true;
                        }
                    }
                }
            }
            return nameexists;
        }

        public static string AutoIncremenetResourceInstance(string resourcetype)
        {
            string resourcetypeupdated = String.Empty;

            return resourcetypeupdated;
        }
    }
}
