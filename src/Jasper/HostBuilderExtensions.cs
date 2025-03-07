using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using Jasper.Configuration;
using Jasper.Logging;
using Jasper.Persistence.Durability;
using Jasper.Persistence.Sagas;
using Jasper.Runtime;
using Jasper.Runtime.Handlers;
using Lamar;
using Lamar.Microsoft.DependencyInjection;
using LamarCodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.ObjectPool;
using Oakton;
using Oakton.Descriptions;
using Oakton.Resources;
using OpenTelemetry.Trace;

namespace Jasper;

public static class HostBuilderExtensions
{
    /// <summary>
    ///     Add Jasper to an ASP.Net Core application with optional configuration to Jasper
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="overrides">Programmatically configure Jasper options</param>
    /// <returns></returns>
    public static IHostBuilder UseJasper(this IHostBuilder builder,
        Action<HostBuilderContext, JasperOptions>? overrides = null)
    {
        return builder.UseJasper(new JasperOptions(), overrides);
    }

    /// <summary>
    ///     Add Jasper to an ASP.Net Core application with optional configuration to Jasper
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="overrides">Programmatically configure Jasper options</param>
    /// <returns></returns>
    public static IHostBuilder UseJasper(this IHostBuilder builder, Action<JasperOptions> overrides)
    {
        return builder.UseJasper((_, r) => overrides(r));
    }

    /// <summary>
    ///     Add Jasper to an ASP.Net Core application with a pre-built JasperOptionsBuilder
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="optionsy"></param>
    /// <returns></returns>
    internal static IHostBuilder UseJasper(this IHostBuilder builder, JasperOptions options,
        Action<HostBuilderContext, JasperOptions>? customization = null)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        builder.UseLamar(r => r.Policies.Add(new HandlerScopingPolicy(options.HandlerGraph)));

        builder.ConfigureServices((context, services) =>
        {
            if (services.Any(x => x.ServiceType == typeof(IJasperRuntime)))
            {
                throw new InvalidOperationException(
                    "IHostBuilder.UseJasper() can only be called once per service collection");
            }

            services.AddSingleton<IStatefulResource, EnvelopeStorageResource>();

            services.AddSingleton(s => s.GetRequiredService<IContainer>().CreateServiceVariableSource());

            services.AddSingleton(s =>
            {
                var extensions = s.GetServices<IJasperExtension>();
                foreach (var extension in extensions)
                {
                    extension.Configure(options);
                    options.AppliedExtensions.Add(extension);
                }

                var environment = s.GetService<IHostEnvironment>();
                var directory = environment?.ContentRootPath ?? AppContext.BaseDirectory;

#if DEBUG
                if (directory.EndsWith("Debug", StringComparison.OrdinalIgnoreCase))
                {
                    directory = directory.ParentDirectory()!.ParentDirectory();
                }
                else if (directory.ParentDirectory()!.EndsWith("Debug", StringComparison.OrdinalIgnoreCase))
                {
                    directory = directory.ParentDirectory()!.ParentDirectory()!.ParentDirectory();
                }
#endif

                options.Advanced.CodeGeneration.GeneratedCodeOutputPath = directory!.AppendPath("Internal", "Generated");

                return options;
            });

            services.AddSingleton<IJasperRuntime, JasperRuntime>();

            services.AddSingleton(s => (IStatefulResourceSource)s.GetRequiredService<IJasperRuntime>());

            services.AddSingleton(options.HandlerGraph);
            services.AddSingleton(options.Advanced);

            // The runtime is also a hosted service
            services.AddSingleton(s => (IHostedService)s.GetRequiredService<IJasperRuntime>());

            services.AddSingleton<IMetrics, NulloMetrics>();
            services.MessagingRootService(x => x.MessageLogger);

            services.AddSingleton<IDescribedSystemPart>(s => s.GetRequiredService<JasperOptions>().HandlerGraph);
            services.AddSingleton<IDescribedSystemPart>(s => s.GetRequiredService<JasperOptions>());

            services.AddSingleton<IEnvelopePersistence, NullEnvelopePersistence>();
            services.AddSingleton<InMemorySagaPersistor>();

            services.MessagingRootService(x => x.Pipeline);

            services.AddOptions();
            services.AddLogging();

            services.AddScoped<ICommandBus, CommandBus>();
            services.AddScoped<IMessagePublisher, MessagePublisher>();
            services.AddScoped<IExecutionContext, ExecutionContext>();

            services.AddSingleton<ObjectPoolProvider>(new DefaultObjectPoolProvider());

            // I'm not proud of this code, but you need a non-null
            // Container property to use the codegen
            services.AddSingleton<ICodeFileCollection>(c =>
            {
                var handlers = c.GetRequiredService<HandlerGraph>();
                var container = (IContainer)c;
                handlers.Container = container;

                if (handlers.Rules == null)
                {
                    handlers.CompileAsync(container.GetInstance<JasperOptions>(), container)
                        .GetAwaiter().GetResult();
                }

                handlers.Rules ??= c.GetRequiredService<JasperOptions>().Advanced.CodeGeneration;

                return handlers;
            });

            options.Services.InsertRange(0, services);

            ExtensionLoader.ApplyExtensions(options);

            customization?.Invoke(context, options);

            options.CombineServices(services);

            Debug.WriteLine("foo");
        });

        return builder;
    }

    internal static void MessagingRootService<T>(this IServiceCollection services, Func<IJasperRuntime, T> expression)
        where T : class
    {
        services.AddSingleton(s => expression(s.GetRequiredService<IJasperRuntime>()));
    }


    /// <summary>
    ///     Syntactical sugar to execute the Jasper command line for a configured WebHostBuilder
    /// </summary>
    /// <param name="hostBuilder"></param>
    /// <param name="args"></param>
    /// <returns></returns>
    public static Task<int> RunJasperAsync(this IHostBuilder hostBuilder, string[] args)
    {
        return hostBuilder.RunOaktonCommands(args);
    }

    public static T Get<T>(this IHost host)
    {
        return host.Services.As<IContainer>().GetInstance<T>();
    }

    public static object Get(this IHost host, Type serviceType)
    {
        return host.Services.GetRequiredService(serviceType);
    }

    /// <summary>
    ///     Syntactical sugar for host.Services.GetRequiredService<IMessagePublisher>().Send(message)
    /// </summary>
    /// <param name="host"></param>
    /// <param name="message"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static ValueTask SendAsync<T>(this IHost host, T message, DeliveryOptions? options = null)
    {
        return host.Get<IMessagePublisher>().SendAsync(message, options);
    }

    /// <summary>
    ///     Syntactical sugar for host.Services.GetRequiredService<IMessagePublisher>().Send(message)
    /// </summary>
    /// <param name="host"></param>
    /// <param name="endpointName"></param>
    /// <param name="message"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static ValueTask SendToEndpointAsync<T>(this IHost host, string endpointName, T message,
        DeliveryOptions? options = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        return host.Get<IMessagePublisher>().SendToEndpointAsync(endpointName, message, options);
    }

    /// <summary>
    /// Syntactical sugar to invoke a single message with the registered
    /// Jasper command bus for this host
    /// </summary>
    /// <param name="host"></param>
    /// <param name="command"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static Task InvokeAsync<T>(this IHost host, T command)
    {
        return host.Get<ICommandBus>().InvokeAsync(command!);
    }

    /// <summary>
    ///     Add Jasper tracing to your Open Telemetry diagnostic publishing
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static TracerProviderBuilder AddJasper(this TracerProviderBuilder builder)
    {
        return builder.AddSource("Jasper");
    }
}
