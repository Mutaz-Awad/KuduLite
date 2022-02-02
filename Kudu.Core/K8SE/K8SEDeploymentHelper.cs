﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Caching;
using System.Text;
using k8s;
using Kudu.Contracts.Deployment;
using Kudu.Contracts.Infrastructure;
using Kudu.Contracts.Settings;
using Kudu.Contracts.Tracing;
using Kudu.Core.Deployment;
using Kudu.Core.Functions;
using Kudu.Core.Kube;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;

namespace Kudu.Core.K8SE
{
    public static class K8SEDeploymentHelper
    {

        public static ITracer _tracer;
        public static ILogger _logger;
        private static ObjectCache cache = MemoryCache.Default;
        private static CacheItemPolicy instanceCachePolicy = new CacheItemPolicy
        {
            AbsoluteExpiration = DateTimeOffset.Now.AddSeconds(30.0),

        };

        // K8SE_BUILD_SERVICE not null or empty
        public static bool IsK8SEEnvironment()
        {
            return !String.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(Constants.IsK8SEEnvironment));
        }

        /// <summary>
        /// Calls into buildctl to retrieve BuildVersion of
        /// the K8SE App
        /// </summary>
        /// <param name="appName"></param>
        /// <returns></returns>
        public static string GetLinuxFxVersion(string appName)
        {
            var cmd = new StringBuilder();
            BuildCtlArgumentsHelper.AddBuildCtlCommand(cmd, "get");
            BuildCtlArgumentsHelper.AddAppNameArgument(cmd, appName);
            BuildCtlArgumentsHelper.AddAppPropertyArgument(cmd, "linuxFxVersion");
            return RunBuildCtlCommand(cmd.ToString(), "Retrieving framework info...");
        }

        /// <summary>
        /// Calls into buildctl to get a list of instaces for an app
        /// </summary>
        /// <param name="appName"></param>
        /// <returns></returns>
        public static List<PodInstance> GetInstances(string appName)
        {
            var cachedInstances = cache.Get(appName);
            if (cachedInstances == null)
            {
                var cmd = new StringBuilder();
                BuildCtlArgumentsHelper.AddBuildCtlCommand(cmd, "get");
                BuildCtlArgumentsHelper.AddAppNameArgument(cmd, appName);
                BuildCtlArgumentsHelper.AddAppPropertyArgument(cmd, "podInstances");
                var instList = RunBuildCtlCommand(cmd.ToString(), "Getting app instances...");
                byte[] data = Convert.FromBase64String(instList);
                string json = Encoding.UTF8.GetString(data);
                cachedInstances = JsonConvert.DeserializeObject<List<PodInstance>>(json);
                cache.Add(appName, cachedInstances, instanceCachePolicy);
            }

            return (List<PodInstance>)cachedInstances;
        }

        /// <summary>
        /// Calls into buildctl to update a BuildVersion of
        /// the K8SE App
        /// </summary>
        /// <param name="appName"></param>
        /// <returns></returns>
        public static void UpdateBuildNumber(string appName, BuildMetadata buildMetadata)
        {
            var cmd = new StringBuilder();
            BuildCtlArgumentsHelper.AddBuildCtlCommand(cmd, "update");
            BuildCtlArgumentsHelper.AddAppNameArgument(cmd, appName);
            BuildCtlArgumentsHelper.AddAppPropertyArgument(cmd, "buildMetadata");
            BuildCtlArgumentsHelper.AddAppPropertyValueArgument(cmd, $"\\\"{GetBuildMetadataStr(buildMetadata)}\\\"");
            RunBuildCtlCommand(cmd.ToString(), "Updating build version...");
        }

        /// <summary>
        /// Updates the Image Tag of the K8SE custom container app
        /// </summary>
        /// <param name="appName"></param>
        /// <param name="imageTag">container image tag of the format registry/<image>:<tag></param>
        /// <returns></returns>
        public static void UpdateImageTag(string appName, string imageTag)
        {
            var cmd = new StringBuilder();
            BuildCtlArgumentsHelper.AddBuildCtlCommand(cmd, "update");
            BuildCtlArgumentsHelper.AddAppNameArgument(cmd, appName);
            BuildCtlArgumentsHelper.AddAppPropertyArgument(cmd, "appImage");
            BuildCtlArgumentsHelper.AddAppPropertyValueArgument(cmd, imageTag);
            RunBuildCtlCommand(cmd.ToString(), "Updating image tag...");
        }

        /// <summary>
        /// Updates the triggers for the function apps
        /// </summary>
        /// <param name="appName">The app name to update</param>
        /// <param name="functionTriggers">The IEnumerable<ScaleTrigger></param>
        /// <param name="buildNumber">Build number to update</param>
        public static void UpdateFunctionAppTriggers(string appName, IEnumerable<ScaleTrigger> functionTriggers, BuildMetadata buildMetadata)
        {
            var functionAppPatchJson = GetFunctionAppPatchJson(functionTriggers, buildMetadata);
            if (string.IsNullOrEmpty(functionAppPatchJson))
            {
                return;
            }

            var cmd = new StringBuilder();
            BuildCtlArgumentsHelper.AddBuildCtlCommand(cmd, "updatejson");
            BuildCtlArgumentsHelper.AddAppNameArgument(cmd, appName);
            BuildCtlArgumentsHelper.AddFunctionTriggersJsonToPatchValueArgument(cmd, functionAppPatchJson);
            RunBuildCtlCommand(cmd.ToString(), "Updating function app triggers...");
        }

        public static void CreateTriggerAuthenticationRef(string secretName, string authRefSecretKeyToParamMap, string appName)
        {
            var cmd = new StringBuilder();
            BuildCtlArgumentsHelper.AddBuildCtlCommand(cmd, "createTriggerAuth");
            BuildCtlArgumentsHelper.AddSecretName(cmd, secretName);
            BuildCtlArgumentsHelper.AddAppNameArgument(cmd, appName);
            BuildCtlArgumentsHelper.AddAuthRefSecretKeyToParamMap(cmd, authRefSecretKeyToParamMap);
            RunBuildCtlCommand(cmd.ToString(), "Creating Trigger Authentication...");
        }

        private static string RunBuildCtlCommand(string args, string msg)
        {
            Console.WriteLine($"{msg} : {args}");
            var process = new Process()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{args}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            Console.WriteLine($"buildctl output:\n {output}");
            process.WaitForExit();

            if (string.IsNullOrEmpty(error))
            {
                return output;
            }
            else
            {
                throw new Exception(error);
            }
        }

        public static string GetAppName(HttpContext context)
        {
            var appName = context.Request.Headers["K8SE_APP_NAME"].ToString();

            if (string.IsNullOrEmpty(appName))
            {
                context.Response.StatusCode = 401;
                // K8SE TODO: move this to resource map
                throw new InvalidOperationException("Couldn't recognize AppName");
            }
            return appName;
        }

        public static string GetAppKind(HttpContext context)
        {
            var appKind = context.Request.Headers["K8SE_APP_KIND"].ToString();
            //K8SE_APP_KIND is only needed for the logic apps, for web apps and function apps, fallback to "kubeapp"
            appKind = string.IsNullOrEmpty(appKind) ? "kubeapp" : appKind;
            if (string.IsNullOrEmpty(appKind))
            {
                context.Response.StatusCode = 401;
                // K8SE TODO: move this to resource map
                throw new InvalidOperationException("Couldn't recognize AppKind");
            }

            return appKind;
        }

        public static string GetAppNamespace(HttpContext context)
        {
            var appNamepace = context.Request.Headers["K8SE_APP_NAMESPACE"].ToString();
            return appNamepace;
        }

        public static void UpdateContextWithAppSettings(IKubernetes client, HttpContext context)
        {
            try
            {
                var appName = GetAppName(context);
                var appNamespace = GetAppNamespace(context);
                if (string.IsNullOrEmpty(appNamespace))
                {
                    Console.WriteLine("appnamespace null");
                    appNamespace = System.Environment.GetEnvironmentVariable(SettingsKeys.AppsNamespace);
                    Console.WriteLine($"appnamespace {appNamespace}");
                }

                Console.WriteLine(appName);
                Console.WriteLine(appNamespace);

                k8s.Models.V1Secret secret = null;
                KubernetesClientUtil.ExecuteWithRetry(()=>
                {
                    // TODO: should get the secret name from the app defination.
                    secret = client.ReadNamespacedSecret(appName + "-secrets".ToLower(), appNamespace);
                });

                if (secret.Data != null)
                {
                    context.Items.TryAdd("appSettings", secret.Data.ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetString(kv.Value)));
                }
            }
            catch (Exception e)
            {
                if (e.InnerException != null)
                {
                    Console.WriteLine(e.InnerException);
                    Console.WriteLine(e.InnerException.Message);
                    Console.WriteLine(e.InnerException.StackTrace);
                }

                throw;
            }
        }

        private static string GetFunctionAppPatchJson(IEnumerable<ScaleTrigger> functionTriggers, BuildMetadata buildMetadata)
        {
            if ((functionTriggers == null || !functionTriggers.Any()) && buildMetadata == null)
            {
                return null;
            }

            var patchAppJson = new PatchAppJson { PatchSpec = new PatchSpec { } };
            if (functionTriggers?.Any() == true)
            {
                patchAppJson.PatchSpec.TriggerOptions = new TriggerOptions
                {
                    Triggers = functionTriggers
                };
            }

            if (buildMetadata != null)
            {
                patchAppJson.PatchSpec.Code = new CodeSpec
                {
                    PackageRef = new PackageReference
                    {
                        BuildMetadata = GetBuildMetadataStr(buildMetadata),
                    }
                };
            }

            var str= System.Convert.ToBase64String(ASCIIEncoding.ASCII.GetBytes(JsonConvert.SerializeObject(patchAppJson)));
            Console.WriteLine("Test Str:     " + str);
            return str;
        }
        private static string GetBuildMetadataStr(BuildMetadata buildMetadata)
        {
            return $"{buildMetadata.AppName}|{buildMetadata.BuildVersion}|{System.Convert.ToBase64String(ASCIIEncoding.ASCII.GetBytes(JsonConvert.SerializeObject(buildMetadata)))}";
        }

        public static bool IsBuildJob()
        {
            var vaule = System.Environment.GetEnvironmentVariable(Constants.IsBuildJob);
            return StringUtils.IsTrueLike(vaule);
        }

        public static bool UseBuildJob()
        {
            var vaule = System.Environment.GetEnvironmentVariable(Constants.UseBuildJob);
            return StringUtils.IsTrueLike(vaule);
        }

        public static void UpdateKubernetesSecrets(IKubernetes _kubernetesClient, IDictionary<string, string> secretData, string secretName, string secretNamespace) {

            try {
                IDictionary<string, IDictionary<string, string>> stringData = new Dictionary<string, IDictionary<string, string>>();
                stringData.Add("stringData", secretData);
                string jsonString = JsonConvert.SerializeObject(stringData);
                k8s.Models.V1Patch body = new k8s.Models.V1Patch(jsonString, k8s.Models.V1Patch.PatchType.MergePatch);

                KubernetesClientUtil.ExecuteWithRetry(()=>
                    {
                        _kubernetesClient.PatchNamespacedSecret(body, secretName, secretNamespace);
                    });

            } catch (Exception e) {
               // _logger.LogError("Error in adding secrets to secret file {0} in namespace {1}" , secretName , secretNamespace, LogEntryType.Error);
               Console.WriteLine("Error in adding secrets to secret file {0} in namespace {1}" , secretName , secretNamespace);
               Console.WriteLine(e.InnerException);

               throw e;
            }
        }
    }
}
