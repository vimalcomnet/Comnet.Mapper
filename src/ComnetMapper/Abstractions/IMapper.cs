using System.Collections.Generic;

namespace ComnetMapper.Abstractions
{
    /// <summary>
    /// Defines the core object mapping contract for Mapper.
    /// Inject this interface wherever mapping is needed to keep code
    /// decoupled from the concrete implementation.
    /// </summary>
    public interface IMapper
    {
        /// <summary>
        /// Maps a source object to a new instance of <typeparamref name="TDest"/>.
        /// Handles both single objects and collections automatically.
        /// </summary>
        /// <typeparam name="TDest">The destination type to map to.</typeparam>
        /// <param name="source">The source object to map from. Returns default if null.</param>
        /// <returns>A newly created and populated <typeparamref name="TDest"/> instance.</returns>
        TDest Map<TDest>(object source);

        /// <summary>
        /// Maps a source object onto an existing destination instance,
        /// overwriting only the properties that have a registered mapping.
        /// Useful for partial updates (PATCH-style operations).
        /// </summary>
        /// <typeparam name="TSource">The source type.</typeparam>
        /// <typeparam name="TDest">The destination type.</typeparam>
        /// <param name="source">The source object. Returns destination unchanged if null.</param>
        /// <param name="destination">The existing destination to populate.</param>
        /// <returns>The updated destination instance.</returns>
        TDest Map<TSource, TDest>(TSource source, TDest destination);

        /// <summary>
        /// Maps a collection of <typeparamref name="TSource"/> items to a
        /// <see cref="IList{TDest}"/>, mapping each element individually.
        /// </summary>
        /// <typeparam name="TSource">The source element type.</typeparam>
        /// <typeparam name="TDest">The destination element type.</typeparam>
        /// <param name="source">The source enumerable. Returns an empty list if null.</param>
        /// <returns>A list of mapped destination objects.</returns>
        IList<TDest> MapList<TSource, TDest>(IEnumerable<TSource> source);

        /// <summary>
        /// Attempts to map a source object to <typeparamref name="TDest"/>, catching
        /// any mapping exception and returning a result object instead of throwing.
        /// Useful for fire-and-forget / non-critical mapping scenarios.
        /// </summary>
        /// <typeparam name="TDest">The destination type.</typeparam>
        /// <param name="source">The source object to map.</param>
        /// <param name="result">The mapping result containing the mapped value and any error.</param>
        /// <returns><c>true</c> if mapping succeeded; <c>false</c> otherwise.</returns>
        bool TryMap<TDest>(object source, out MapResult<TDest> result);
    }

    /// <summary>
    /// Holds the outcome of a safe mapping operation performed by
    /// <see cref="IMapper.TryMap{TDest}"/>.
    /// </summary>
    /// <typeparam name="T">The destination type.</typeparam>
    public sealed class MapResult<T>
    {
        /// <summary>Gets the successfully mapped destination value, or <c>default</c> on failure.</summary>
        public T Value { get; init; }

        /// <summary>Gets a value indicating whether the mapping succeeded.</summary>
        public bool IsSuccess { get; init; }

        /// <summary>Gets the error message if mapping failed; <c>null</c> on success.</summary>
        public string Error { get; init; }

        /// <summary>Creates a successful result wrapping the given value.</summary>
        public static MapResult<T> Success(T value) => new() { Value = value, IsSuccess = true };

        /// <summary>Creates a failed result with the given error message.</summary>
        public static MapResult<T> Failure(string error) => new() { IsSuccess = false, Error = error };
    }
}
