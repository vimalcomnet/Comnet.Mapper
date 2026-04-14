using ComnetMapper.Abstractions;
using ComnetMapper.Configuration;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ComnetMapper.Core
{
    /// <summary>
    /// High-performance object-to-object mapper that uses compiled LINQ
    /// expression trees so that mappings pay their reflection cost only once
    /// (at first use) and run at near-native speed on every subsequent call.
    ///
    /// <para>
    /// Drop-in replacement for AutoMapper: swap AutoMapper's
    /// <c>IMapper</c>/<c>MapperConfiguration</c> for <see cref="IMapper"/>
    /// and register profiles with <see cref="AddProfile{T}"/>.
    /// </para>
    ///
    /// <para>Thread-safety: all public methods are thread-safe.</para>
    /// </summary>
    public sealed class Mapper : IMapper
    {
        // ── Internal state ─────────────────────────────────────────────────────

        /// <summary>
        /// Cache of compiled mapping delegates keyed by (sourceType, destType).
        /// Compiled once, reused on every Map call → O(1) amortised cost.
        /// </summary>
        private readonly ConcurrentDictionary<(Type, Type), object> _compiledMapCache = new();

        /// <summary>
        /// Custom per-member source expressions registered via
        /// <c>ForMember(...).MapFrom(...)</c>. Keyed by (source, dest, propertyName).
        /// </summary>
        private readonly ConcurrentDictionary<(Type, Type, string), LambdaExpression> _customMappings = new();

        /// <summary>
        /// Set of destination property names that are explicitly ignored via
        /// <c>ForMember(...).Ignore()</c>. Keyed by (source, dest, propertyName).
        /// </summary>
        private readonly ConcurrentDictionary<(Type, Type, string), bool> _ignoredMembers = new();

        /// <summary>
        /// Before-map actions keyed by (source, dest). Each action receives
        /// (sourceObj, destObj, mapper) and runs before property assignment.
        /// </summary>
        private readonly ConcurrentDictionary<(Type, Type), Delegate> _beforeMapActions = new();

        /// <summary>
        /// After-map actions keyed by (source, dest). Each action receives
        /// (sourceObj, destObj, mapper) and runs after all property assignments.
        /// </summary>
        private readonly ConcurrentDictionary<(Type, Type), Delegate> _afterMapActions = new();

        // ── Profile registration ───────────────────────────────────────────────

        /// <summary>
        /// Registers all mappings defined in a <see cref="MapperProfile"/> subclass.
        /// Creates a temporary instance of the profile and replays its configuration
        /// actions against this mapper.
        /// </summary>
        /// <typeparam name="T">A concrete <see cref="MapperProfile"/> type.</typeparam>
        public void AddProfile<T>() where T : MapperProfile, new()
            => new T().ConfigActions.ForEach(a => a(this));

        /// <summary>
        /// Registers all profiles found in the given assemblies.
        /// Scans for non-abstract types that inherit <see cref="MapperProfile"/>
        /// and have a public parameterless constructor.
        /// Useful for auto-discovery: <c>mapper.AddProfilesFromAssembly(typeof(Startup).Assembly)</c>.
        /// </summary>
        /// <param name="assemblies">One or more assemblies to scan.</param>
        public void AddProfilesFromAssembly(params Assembly[] assemblies)
        {
            var profileType = typeof(MapperProfile);

            foreach (var assembly in assemblies)
            {
                // Find every concrete MapperProfile subclass in the assembly.
                var profileTypes = assembly.GetTypes()
                    .Where(t => t.IsClass
                             && !t.IsAbstract
                             && profileType.IsAssignableFrom(t)
                             && t.GetConstructor(Type.EmptyTypes) != null);

                foreach (var type in profileTypes)
                {
                    // Create an instance and replay its config actions.
                    var profile = (MapperProfile)Activator.CreateInstance(type);
                    profile.ConfigActions.ForEach(a => a(this));
                }
            }
        }

        // ── Internal registration helpers (called by MappingExpression) ────────

        /// <summary>
        /// Pre-compiles the mapping delegate for (TSource → TDest) so that the
        /// first runtime Map call hits the cache. Called during profile loading.
        /// </summary>
        internal void InitializeMap<TSource, TDest>() =>
            _compiledMapCache.GetOrAdd(
                (typeof(TSource), typeof(TDest)),
                k => CompileMapper(k.Item1, k.Item2));

        /// <summary>
        /// Stores a custom member-level source expression and invalidates the
        /// compiled cache for that (source, dest) pair so it is recompiled
        /// with the new expression on the next Map call.
        /// </summary>
        internal void AddCustomMemberMap<TSource, TDest>(string propName, LambdaExpression mapping)
        {
            _customMappings[(typeof(TSource), typeof(TDest), propName)] = mapping;
            // Invalidate so the next Map call recompiles with this expression included.
            _compiledMapCache.TryRemove((typeof(TSource), typeof(TDest)), out _);
        }

        /// <summary>
        /// Marks a destination property as ignored and invalidates the compiled cache.
        /// </summary>
        internal void AddIgnoredMember<TSource, TDest>(string propName)
        {
            _ignoredMembers[(typeof(TSource), typeof(TDest), propName)] = true;
            _compiledMapCache.TryRemove((typeof(TSource), typeof(TDest)), out _);
        }

        /// <summary>Stores a before-map action delegate for (TSource → TDest).</summary>
        internal void AddBeforeMapAction<TSource, TDest>(Action<TSource, TDest, Mapper> action)
            => _beforeMapActions[(typeof(TSource), typeof(TDest))] = action;

        /// <summary>Stores an after-map action delegate for (TSource → TDest).</summary>
        internal void AddAfterMapAction<TSource, TDest>(Action<TSource, TDest, Mapper> action)
            => _afterMapActions[(typeof(TSource), typeof(TDest))] = action;

        // ── Public mapping API ─────────────────────────────────────────────────

        /// <inheritdoc/>
        public TDest Map<TDest>(object source)
        {
            if (source == null) return default;

            var destType = typeof(TDest);

            // When TDest is a generic collection (List<T>, IList<T>, IEnumerable<T>),
            // map each element individually and return a list.
            if (typeof(IEnumerable).IsAssignableFrom(destType)
                && destType.IsGenericType
                && destType != typeof(string))
            {
                return MapCollection<TDest>(source, destType);
            }

            return Map(source, Activator.CreateInstance<TDest>());
        }

        /// <inheritdoc/>
        public TDest Map<TSource, TDest>(TSource source, TDest destination)
        {
            if (source == null) return destination;

            var sourceType = source.GetType();
            var destType = typeof(TDest);
            var key = (sourceType, destType);

            // Invoke before-map action if one is registered for this pair.
            if (_beforeMapActions.TryGetValue(key, out var beforeDelegate)
                && beforeDelegate is Action<TSource, TDest, Mapper> beforeAction)
            {
                beforeAction((TSource)(object)source, destination, this);
            }

            // Retrieve (or compile on first use) the mapping delegate.
            var func = (Func<object, TDest, Mapper, TDest>)
                _compiledMapCache.GetOrAdd(key, k => CompileMapper(k.Item1, k.Item2));

            var result = func(source, destination, this);

            // Invoke after-map action if one is registered for this pair.
            if (_afterMapActions.TryGetValue(key, out var afterDelegate)
                && afterDelegate is Action<TSource, TDest, Mapper> afterAction)
            {
                afterAction((TSource)(object)source, result, this);
            }

            return result;
        }

        /// <inheritdoc/>
        public IList<TDest> MapList<TSource, TDest>(IEnumerable<TSource> source)
        {
            // Guard against null sources — return an empty list rather than throwing.
            var safeSource = source?.ToList() ?? [];
            return [.. safeSource.Select(x => Map<TDest>((object)x))];
        }

        /// <inheritdoc/>
        public bool TryMap<TDest>(object source, out MapResult<TDest> result)
        {
            try
            {
                var mapped = Map<TDest>(source);
                result = MapResult<TDest>.Success(mapped);
                return true;
            }
            catch (Exception ex)
            {
                // Return a structured failure result instead of propagating.
                result = MapResult<TDest>.Failure(ex.Message);
                return false;
            }
        }

        // ── Collection helper ──────────────────────────────────────────────────

        /// <summary>
        /// Maps a source enumerable to a generic collection of <typeparamref name="TDest"/>.
        /// Uses reflection to invoke the generic <see cref="Map{TDest}(object)"/> overload
        /// per element so nested type resolution works correctly.
        /// </summary>
        private TDest MapCollection<TDest>(object source, Type destType)
        {
            var itemType = destType.GetGenericArguments()[0];
            var sourceItems = (IEnumerable)source;

            // Create a List<itemType> dynamically.
            var listType = typeof(List<>).MakeGenericType(itemType);
            var list = (IList)Activator.CreateInstance(listType);

            // Grab the Map<TDest>(object) overload once and reuse.
            var mapMethod = GetType().GetMethods()
                .First(m => m.Name == nameof(Map)
                         && m.IsGenericMethod
                         && m.GetParameters().Length == 1)
                .MakeGenericMethod(itemType);

            foreach (var item in sourceItems)
                list.Add(mapMethod.Invoke(this, [item]));

            return (TDest)list;
        }

        // ── Expression tree compiler ───────────────────────────────────────────

        /// <summary>
        /// Builds and compiles a strongly-typed mapping delegate for the given
        /// (sourceType → destType) pair using LINQ expression trees.
        ///
        /// <para>
        /// The generated delegate signature is:
        /// <c>Func&lt;object, TDest, Mapper, TDest&gt;</c>
        /// which allows passing the mapper instance for nested-object recursion.
        /// </para>
        ///
        /// <para>
        /// Property matching order:
        /// <list type="number">
        ///   <item>Ignored properties are skipped.</item>
        ///   <item>Custom <c>MapFrom</c> expressions take priority.</item>
        ///   <item>Same-name convention matching (case-insensitive).</item>
        /// </list>
        /// </para>
        /// </summary>
        private object CompileMapper(Type sourceType, Type destType)
        {
            // Parameters of the compiled lambda.
            var sourceParam = Expression.Parameter(typeof(object), "srcObj");
            var sourceTyped = Expression.Convert(sourceParam, sourceType); // cast object → TSource
            var destParam = Expression.Parameter(destType, "dest");
            var mapperParam = Expression.Parameter(typeof(Mapper), "mapper");

            var bindings = new List<Expression>();

            foreach (var destProp in destType.GetProperties().Where(p => p.CanWrite))
            {
                // Skip members explicitly marked as Ignored.
                if (_ignoredMembers.ContainsKey((sourceType, destType, destProp.Name)))
                    continue;

                Expression assignmentValue = null;

                if (_customMappings.TryGetValue((sourceType, destType, destProp.Name), out var customExpr))
                {
                    // --- Custom MapFrom expression ---
                    // Replace the lambda's parameter with our typed source variable so
                    // the expression tree operates on the actual source object.
                    var visitor = new ParameterReplacerVisitor(customExpr.Parameters[0], sourceTyped);
                    assignmentValue = visitor.Visit(customExpr.Body);
                }
                else
                {
                    // --- Convention-based: match by name (case-insensitive) ---
                    var sourceProp = sourceType.GetProperty(
                        destProp.Name,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                    if (sourceProp != null)
                    {
                        assignmentValue = BuildPropertyAssignment(
                            sourceTyped, sourceProp, destProp.PropertyType, mapperParam);
                    }
                }

                if (assignmentValue != null)
                {
                    bindings.Add(
                        Expression.Assign(
                            Expression.Property(destParam, destProp),
                            Expression.Convert(assignmentValue, destProp.PropertyType)
                        )
                    );
                }
            }

            // The block must return the destination object.
            bindings.Add(destParam);

            // Build Func<object, TDest, Mapper, TDest>.
            var delegateType = typeof(Func<,,,>)
                .MakeGenericType(typeof(object), destType, typeof(Mapper), destType);

            return Expression
                .Lambda(delegateType, Expression.Block(bindings), sourceParam, destParam, mapperParam)
                .Compile();
        }

        /// <summary>
        /// Builds the assignment expression for a single source property,
        /// handling the full matrix of type-compatibility scenarios:
        /// direct assignment, nullable unwrapping, enum conversion,
        /// implicit/explicit operator conversion, collection mapping,
        /// and recursive nested-object mapping.
        /// </summary>
        private Expression BuildPropertyAssignment(
            Expression sourceTyped,
            PropertyInfo sourceProp,
            Type dPropType,
            Expression mapperParam)
        {
            var sProp = Expression.Property(sourceTyped, sourceProp);
            var sPropType = sourceProp.PropertyType;

            // 1. Direct assignment or T → T? widening.
            if (dPropType.IsAssignableFrom(sPropType)
                || Nullable.GetUnderlyingType(dPropType) == sPropType)
            {
                return Expression.Convert(sProp, dPropType);
            }

            // 2. Nullable<T> → T narrowing (e.g. int? source → int dest).
            if (Nullable.GetUnderlyingType(sPropType) == dPropType)
            {
                return Expression.Condition(
                    Expression.Property(sProp, "HasValue"),
                    Expression.Property(sProp, "Value"),
                    Expression.Default(dPropType)
                );
            }

            // 3. Enum ↔ numeric or enum ↔ enum.
            if (dPropType.IsEnum || sPropType.IsEnum)
                return Expression.Convert(sProp, dPropType);

            // 4. Primitive numeric widening/narrowing via Convert (int→long, etc.).
            if (IsPrimitiveNumeric(sPropType) && IsPrimitiveNumeric(dPropType))
                return Expression.Convert(sProp, dPropType);

            // 5. Implicit / explicit conversion operators.
            if (HasConversionOperator(sPropType, dPropType))
                return Expression.Convert(sProp, dPropType);

            // 6. IEnumerable<TSrcItem> → IList<TDestItem> via MapList.
            if (typeof(IEnumerable).IsAssignableFrom(sPropType)
                && sPropType != typeof(string)
                && dPropType.IsGenericType)
            {
                return BuildCollectionMapping(sProp, sPropType, dPropType, mapperParam);
            }

            // 7. Nested complex object → recursively Map via the mapper instance.
            if (!dPropType.IsValueType && dPropType != typeof(string))
            {
                return BuildNestedObjectMapping(sProp, sPropType, dPropType, mapperParam);
            }

            return null; // No compatible mapping found — skip this property.
        }

        /// <summary>
        /// Builds an expression that calls <see cref="MapList{TSource,TDest}"/>
        /// on the mapper instance for collection properties.
        /// </summary>
        private Expression BuildCollectionMapping(
            Expression sProp, Type sPropType, Type dPropType, Expression mapperParam)
        {
            var srcItemType = sPropType.IsGenericType
                ? sPropType.GetGenericArguments()[0]
                : typeof(object);

            var destItemType = dPropType.GetGenericArguments()[0];

            var mapListMethod = typeof(Mapper)
                .GetMethod(nameof(MapList))
                .MakeGenericMethod(srcItemType, destItemType);

            var enumerableType = typeof(IEnumerable<>).MakeGenericType(srcItemType);

            return Expression.Call(
                mapperParam,
                mapListMethod,
                Expression.Convert(sProp, enumerableType)
            );
        }

        /// <summary>
        /// Builds an expression that calls <see cref="Map{TDest}(object)"/> on the
        /// mapper instance for nested complex-type properties, guarded by a null check.
        /// </summary>
        private static Expression BuildNestedObjectMapping(
            Expression sProp, Type sPropType, Type dPropType, Expression mapperParam)
        {
            var mapMethod = typeof(Mapper).GetMethods()
                .First(m => m.Name == nameof(Map)
                         && m.IsGenericMethod
                         && m.GetParameters().Length == 1
                         && m.GetParameters()[0].ParameterType == typeof(object))
                .MakeGenericMethod(dPropType);

            // Guard: if source nested property is null, yield default(TDest).
            return Expression.Condition(
                Expression.Equal(sProp, Expression.Constant(null, sPropType)),
                Expression.Default(dPropType),
                Expression.Call(
                    mapperParam,
                    mapMethod,
                    Expression.Convert(sProp, typeof(object))
                )
            );
        }

        // ── Type helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> if the type is one of the built-in numeric primitives.
        /// Used to allow safe widening/narrowing numeric conversions.
        /// </summary>
        private static bool IsPrimitiveNumeric(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            return u == typeof(byte) || u == typeof(sbyte)
                || u == typeof(short) || u == typeof(ushort)
                || u == typeof(int) || u == typeof(uint)
                || u == typeof(long) || u == typeof(ulong)
                || u == typeof(float) || u == typeof(double)
                || u == typeof(decimal);
        }

        /// <summary>
        /// Returns <c>true</c> if there is an implicit or explicit conversion operator
        /// defined on either <paramref name="from"/> or <paramref name="to"/> that
        /// converts from → to.
        /// </summary>
        private static bool HasConversionOperator(Type from, Type to)
        {
            if (to.IsAssignableFrom(from)) return true;

            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;

            return from.GetMethods(flags)
                .Concat(to.GetMethods(flags))
                .Any(m => (m.Name == "op_Implicit" || m.Name == "op_Explicit")
                       && m.ReturnType == to
                       && m.GetParameters()[0].ParameterType == from);
        }

        // ── Expression visitor ─────────────────────────────────────────────────

        /// <summary>
        /// Expression visitor that replaces occurrences of <c>oldParam</c>
        /// with <c>newExpression</c> throughout an expression tree, and
        /// automatically materialises lazy LINQ calls (Select, Where, SelectMany)
        /// to <see cref="List{T}"/> so that deferred-execution sequences don't
        /// escape the compiled delegate boundary.
        /// </summary>
        private sealed class ParameterReplacerVisitor(
            ParameterExpression oldParam,
            Expression newExpression) : ExpressionVisitor
        {
            private readonly ParameterExpression _oldParam = oldParam;
            private readonly Expression _newExpression = newExpression;

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                // Materialize deferred LINQ sequences to avoid "enumeration has changed"
                // or lazy-evaluation issues inside compiled delegates.
                if (node.Method.Name is "Select" or "Where" or "SelectMany")
                {
                    var visited = base.VisitMethodCall(node);

                    var elementType = visited.Type.IsGenericType
                        ? visited.Type.GetGenericArguments()[0]
                        : typeof(object);

                    var toListMethod = typeof(Enumerable)
                        .GetMethod(nameof(Enumerable.ToList))
                        .MakeGenericMethod(elementType);

                    // Wrap the LINQ chain in .ToList() to materialise it.
                    return Expression.Call(toListMethod, visited);
                }

                return base.VisitMethodCall(node);
            }

            protected override Expression VisitParameter(ParameterExpression node)
                => node == _oldParam ? _newExpression : base.VisitParameter(node);
        }
    }
}
