using ComnetMapper.Core;
using System;
using System.Collections.Generic;
using System.Text;

namespace ComnetMapper.Extensions
{
    public static class MapperExtensions
    {
        private static Mapper? _mapper;

        public static void InitializeMapper(this Mapper mapper)
        {
            _mapper = mapper;
        }

        #region Utilities

        /// <summary>
        /// Maps source to TDestination. Supports classes and IList interfaces.
        /// </summary>
        public static TDestination MapTo<TDestination>(this object source)
        {
            if (_mapper == null)
                throw new InvalidOperationException("Mapper not initialized! Call InitializeMapper during startup.");

            return _mapper.Map<TDestination>(source);
        }

        public static TDestination MapTo<TSource, TDestination>(this TSource source, TDestination destination)
        {
            if (_mapper == null)
                throw new InvalidOperationException("Mapper not initialized!");

            return _mapper.Map(source, destination);
        }

        #endregion
    }
}
