using ComnetMapper.Abstractions;
using ComnetMapper.Core;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Reflection;

namespace ComnetMapper.Extensions
{
    /// <summary>
    /// Extension methods for <see cref="IServiceCollection"/> that register
    /// <see cref="Mapper"/> (and its <see cref="IMapper"/> interface)
    /// as a singleton in the dependency injection container.
    /// </summary>
    public static class MapperServiceExtensions
    {
        /// <summary>
        /// Registers <see cref="Mapper"/> as a singleton and configures it
        /// using a callback. Use this overload when you configure mappings inline
        /// rather than via profile classes.
        /// </summary>
        /// <param name="services">The application's service collection.</param>
        /// <param name="configuration">
        /// A callback that receives the mapper instance and should call
        /// <c>mapper.AddProfile&lt;T&gt;()</c> to register profiles.
        /// </param>
        /// <returns>The service collection for fluent chaining.</returns>
        /// <example>
        /// <code>
        /// builder.Services.AddMapper(mapper =>
        /// {
        ///     mapper.AddProfile&lt;OrderProfile&gt;();
        ///     mapper.AddProfile&lt;CustomerProfile&gt;();
        /// });
        /// </code>
        /// </example>
        public static IServiceCollection AddMapper(this IServiceCollection services, Action<Mapper> configuration)
        {
            var mapper = new Mapper();
            configuration(mapper);

            // Register the concrete type AND the interface so both can be injected.
            services.AddSingleton(mapper);
            services.AddSingleton<IMapper>(mapper);
            MapperExtensions.InitializeMapper(mapper);
            return services;
        }

        /// <summary>
        /// Registers <see cref="Mapper"/> as a singleton and automatically
        /// discovers all <c>MapperProfile</c> subclasses in the given assemblies.
        /// Ideal for projects with many profiles — no need to list them individually.
        /// </summary>
        /// <param name="services">The application's service collection.</param>
        /// <param name="assemblies">
        /// One or more assemblies to scan for <c>MapperProfile</c> subclasses.
        /// </param>
        /// <returns>The service collection for fluent chaining.</returns>
        /// <example>
        /// <code>
        /// builder.Services.AddMapperFromAssemblies(typeof(Program).Assembly);
        /// </code>
        /// </example>
        public static IServiceCollection AddMapperFromAssemblies(
            this IServiceCollection services,
            params Assembly[] assemblies)
        {
            var mapper = new Mapper();
            mapper.AddProfilesFromAssembly(assemblies);

            services.AddSingleton(mapper);
            services.AddSingleton<IMapper>(mapper);

            return services;
        }
    }
}
