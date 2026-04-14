using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace ComnetMapper.Configuration
{
    /// <summary>
    /// Base class for defining object mapping profiles.
    /// Inherit from this class to group related mappings together,
    /// exactly like AutoMapper profiles — just swap <c>AutoMapper.Profile</c>
    /// for <c>MapperProfile</c> in your existing profiles.
    /// </summary>
    /// <example>
    /// <code>
    /// public class OrderProfile : MapperProfile
    /// {
    ///     public OrderProfile()
    ///     {
    ///         CreateMap&lt;Order, OrderDto&gt;()
    ///             .ForMember(d => d.CustomerName, o => o.MapFrom(s => s.Customer.FullName))
    ///             .ReverseMap();
    ///     }
    /// }
    /// </code>
    /// </example>
    public abstract class MapperProfile
    {
        /// <summary>
        /// Ordered list of configuration actions that will be applied to
        /// the mapper instance when <see cref="Core.Mapper.AddProfile{T}"/> is called.
        /// </summary>
        internal List<Action<Core.Mapper>> ConfigActions { get; } = [];

        /// <summary>
        /// Registers a mapping from <typeparamref name="TSource"/> to
        /// <typeparamref name="TDest"/> and returns a fluent expression
        /// for further member-level configuration.
        /// </summary>
        /// <typeparam name="TSource">The source type.</typeparam>
        /// <typeparam name="TDest">The destination type.</typeparam>
        /// <returns>
        /// A <see cref="MappingExpression{TSource,TDest}"/> for chaining
        /// <c>ForMember</c> and <c>ReverseMap</c> calls.
        /// </returns>
        protected MappingExpression<TSource, TDest> CreateMap<TSource, TDest>()
        {
            // Queue the initialization so maps compile after all ForMember
            // calls have been registered (avoids stale cache entries).
            ConfigActions.Add(m => m.InitializeMap<TSource, TDest>());
            return new MappingExpression<TSource, TDest>(ConfigActions);
        }

        // ── Fluent API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Fluent builder for configuring individual member mappings and
        /// generating the reverse map. Mirrors the AutoMapper API surface.
        /// </summary>
        public class MappingExpression<TSource, TDest>(List<Action<Core.Mapper>> actions)
        {
            private readonly List<Action<Core.Mapper>> _actions = actions;

            /// <summary>
            /// Configures a custom mapping for a single destination member.
            /// </summary>
            /// <typeparam name="TMember">The type of the destination member.</typeparam>
            /// <param name="destinationMember">
            /// Lambda pointing to the destination property, e.g. <c>d => d.FullName</c>.
            /// </param>
            /// <param name="memberOptions">
            /// Configuration action — call <c>opt.MapFrom(s => s.SomeProp)</c>
            /// or <c>opt.Ignore()</c> inside this action.
            /// </param>
            /// <returns>The current expression for fluent chaining.</returns>
            public MappingExpression<TSource, TDest> ForMember<TMember>(
                Expression<Func<TDest, TMember>> destinationMember,
                Action<MemberConfigurationExpression<TSource, TMember>> memberOptions)
            {
                // Extract the destination property name from the lambda body.
                var memberExpr = (MemberExpression)destinationMember.Body;
                var propertyName = memberExpr.Member.Name;

                var options = new MemberConfigurationExpression<TSource, TMember>();
                memberOptions(options);

                if (options.Ignored)
                {
                    // Mark the property as explicitly ignored so it is skipped
                    // even when a same-name source property exists.
                    _actions.Add(m => m.AddIgnoredMember<TSource, TDest>(propertyName));
                }
                else if (options.MapFromExpression != null)
                {
                    // Register the custom expression mapping.
                    _actions.Add(m => m.AddCustomMemberMap<TSource, TDest>(propertyName, options.MapFromExpression));
                }

                return this;
            }

            /// <summary>
            /// Adds a before-map action that runs before any property assignments.
            /// Useful for validation, logging, or pre-processing.
            /// </summary>
            /// <param name="beforeAction">
            /// An action receiving (source, destination, mapper) before mapping begins.
            /// </param>
            public MappingExpression<TSource, TDest> BeforeMap(Action<TSource, TDest, Core.Mapper> beforeAction)
            {
                _actions.Add(m => m.AddBeforeMapAction<TSource, TDest>(beforeAction));
                return this;
            }

            /// <summary>
            /// Adds an after-map action that runs after all property assignments.
            /// Useful for computed properties or post-processing.
            /// </summary>
            /// <param name="afterAction">
            /// An action receiving (source, destination, mapper) after mapping completes.
            /// </param>
            public MappingExpression<TSource, TDest> AfterMap(Action<TSource, TDest, Core.Mapper> afterAction)
            {
                _actions.Add(m => m.AddAfterMapAction<TSource, TDest>(afterAction));
                return this;
            }

            /// <summary>
            /// No-op stub kept for AutoMapper drop-in compatibility.
            /// Inaccessible setters are simply skipped automatically.
            /// </summary>
            public MappingExpression<TSource, TDest> IgnoreAllPropertiesWithAnInaccessibleSetter() => this;

            /// <summary>
            /// Also registers the inverse mapping from <typeparamref name="TDest"/>
            /// back to <typeparamref name="TSource"/> using convention-based matching.
            /// Custom <c>ForMember</c> expressions are NOT reversed automatically —
            /// add explicit <c>ForMember</c> calls on the returned expression if needed.
            /// </summary>
            /// <returns>A new expression for configuring the reverse mapping.</returns>
            public MappingExpression<TDest, TSource> ReverseMap()
            {
                _actions.Add(m => m.InitializeMap<TDest, TSource>());
                return new MappingExpression<TDest, TSource>(_actions);
            }
        }

        /// <summary>
        /// Configuration options for a single destination member.
        /// Used inside the <c>ForMember</c> callback.
        /// </summary>
        public class MemberConfigurationExpression<TSource, TMember>
        {
            /// <summary>Gets the custom source expression, or <c>null</c> when not set.</summary>
            internal LambdaExpression MapFromExpression { get; private set; }

            /// <summary>Gets a value indicating whether this member should be ignored.</summary>
            internal bool Ignored { get; private set; }

            /// <summary>
            /// Maps this destination member using the specified source expression.
            /// </summary>
            /// <typeparam name="TSourceMember">The type returned by the source expression.</typeparam>
            /// <param name="mapExpression">
            /// A lambda that produces the destination value from the source, e.g.
            /// <c>s => s.FirstName + " " + s.LastName</c>.
            /// </param>
            public void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> mapExpression)
                => MapFromExpression = mapExpression;

            /// <summary>
            /// Excludes this destination member from mapping entirely.
            /// The property will retain whatever value it had before <c>Map</c> was called.
            /// </summary>
            public void Ignore() => Ignored = true;
        }
    }
}
