﻿using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using k8s;
using Kudu.Contracts.Settings;
using Kudu.Contracts.SourceControl;
using Kudu.Contracts.Tracing;
using Kudu.Core;
using Kudu.Core.Deployment;
using Kudu.Core.Deployment.Generator;
using Kudu.Core.Extensions;
using Kudu.Core.Hooks;
using Kudu.Core.K8SE;
using Kudu.Core.Kube;
using Kudu.Core.Settings;
using Kudu.Core.SourceControl.Git;
using Kudu.Core.Tracing;
using Kudu.Services.Performance;
using Kudu.Services.ServiceHookHandlers;
using Kudu.Services.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using XmlSettings;

namespace Kudu.Services.Web
{
    public static class ServiceExtensions
    {
        internal static void AddGitServiceHookParsers(this IServiceCollection services)
        {
            services.AddScoped<IServiceHookHandler, GenericHandler>();
            services.AddScoped<IServiceHookHandler, GitHubHandler>();
            services.AddScoped<IServiceHookHandler, BitbucketHandler>();
            services.AddScoped<IServiceHookHandler, BitbucketHandlerV2>();
            services.AddScoped<IServiceHookHandler, DropboxHandler>();
            services.AddScoped<IServiceHookHandler, CodePlexHandler>();
            services.AddScoped<IServiceHookHandler, CodebaseHqHandler>();
            services.AddScoped<IServiceHookHandler, GitlabHqHandler>();
            services.AddScoped<IServiceHookHandler, GitHubCompatHandler>();
            services.AddScoped<IServiceHookHandler, KilnHgHandler>();
            services.AddScoped<IServiceHookHandler, VSOHandler>();
            services.AddScoped<IServiceHookHandler, OneDriveHandler>();
        }

        internal static void AddLogStreamService(this IServiceCollection services, 
            IEnvironment environment)
        {
            services.AddTransient(sp =>
            {
                var env = sp.GetEnvironment(environment);
                var traceFactory = sp.GetRequiredService<ITraceFactory>();
                var logStreamManagerLock = KuduWebUtil.GetNamedLocks(traceFactory, env)[Constants.HooksLockName];
                return new LogStreamManager(Path.Combine(environment.RootPath, Constants.LogFilesPath),
                    sp.GetRequiredService<IEnvironment>(),
                    sp.GetRequiredService<IDeploymentSettingsManager>(),
                    sp.GetRequiredService<ITracer>(),
                    logStreamManagerLock);
            });
        }
        
        internal static void AddGitServer(this IServiceCollection services, IEnvironment environment)
        {
            services.AddTransient<IDeploymentEnvironment, DeploymentEnvironment>();
            services.AddScoped<IGitServer>(sp =>
            {
                var env = sp.GetEnvironment(environment);
                var traceFactory = sp.GetRequiredService<ITraceFactory>();
                var deploymentLock = KuduWebUtil.GetDeploymentLock(traceFactory, env);
                return new GitExeServer(
                    sp.GetRequiredService<IEnvironment>(),
                    deploymentLock,
                    KuduWebUtil.GetRequestTraceFile(sp),
                    sp.GetRequiredService<IRepositoryFactory>(),
                    sp.GetRequiredService<IDeploymentEnvironment>(),
                    sp.GetRequiredService<IDeploymentSettingsManager>(),
                    traceFactory);
            });
        }

        internal static void AddGZipCompression(this IServiceCollection services)
        {
            services.Configure<GzipCompressionProviderOptions>(options =>
                options.Level = CompressionLevel.Optimal);
            services.AddResponseCompression();
        }

        internal static void AddWebJobsDependencies(this IServiceCollection services)
        {
            //IContinuousJobsManager continuousJobManager = new AggregateContinuousJobsManager(
            //    etwTraceFactory,
            //    kernel.Get<IEnvironment>(),
            //    kernel.Get<IDeploymentSettingsManager>(),
            //    kernel.Get<IAnalytics>());

            //OperationManager.SafeExecute(triggeredJobsManager.CleanupDeletedJobs);
            //OperationManager.SafeExecute(continuousJobManager.CleanupDeletedJobs);

            //kernel.Bind<IContinuousJobsManager>().ToConstant(continuousJobManager)
            //                     .InTransientScope();
        }

        internal static void AddDeploymentServices(this IServiceCollection services)
        {
            services.AddScoped<ISettings>(sp =>
            {
                var env = sp.GetRequiredService<IEnvironment>();
                return new XmlSettings.Settings(KuduWebUtil.GetSettingsPath(env));
            });
            services.AddScoped<IDeploymentSettingsManager, DeploymentSettingsManager>(sp =>
            {
                var k8sClient = sp.GetRequiredService<IKubernetes>();
                var httpcontext = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
                K8SEDeploymentHelper.UpdateContextWithAppSettings(k8sClient, httpcontext);

                var manager = new DeploymentSettingsManager(sp.GetRequiredService<ISettings>(), httpcontext.GetAppSettings());
                var env = sp.GetRequiredService<IEnvironment>();
                KuduWebUtil.UpdateEnvironmentBySettings(env, manager);
                return manager;
            });
            services.AddScoped<IDeploymentStatusManager, DeploymentStatusManager>();
            services.AddScoped<ISiteBuilderFactory, SiteBuilderFactory>();
            services.AddScoped<IWebHooksManager, WebHooksManager>();
        }

        internal static IEnvironment GetEnvironment(this IServiceProvider sp, IEnvironment environment)
        {
            if (K8SEDeploymentHelper.IsBuildJob() || K8SEDeploymentHelper.UseBuildJob())
            {
                return sp.GetRequiredService<IEnvironment>();
            }

            return environment;
        }

        internal static void AddKubernetesClientFactory(this IServiceCollection services)
        {
            services.AddHttpClient("k8s")
                .AddTypedClient<IKubernetes>((httpClient, serviceProvider) =>
                {
                    var config = KubernetesClientConfiguration.BuildDefaultConfig();
                    return new Kubernetes(
                        config,
                        httpClient);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = KubernetesClientUtil.ServerCertificateValidationCallback,
                });
        }
    }
}