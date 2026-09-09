using System;
using System.Dynamic;
using System.Collections.Generic;

namespace Xilium.CefGlue.Common.Shared.Serialization.State
{
    internal class CollectionDeserializerState : IDeserializerState<object>
    {
        private readonly JsonTypeInfo _collectionTypeInfo;
        private readonly JsonTypeInfo _collectionElementTypeInfo;
        
        private string _propertyName;

        public CollectionDeserializerState(JsonTypeInfo collectionTypeInfo)
        {
            if (collectionTypeInfo.CollectionAddMethod == null)
            {
                throw new ArgumentException("Argument must contain an Add method.", nameof(collectionTypeInfo));
            }

            var collectionType = collectionTypeInfo.ObjectType;
            if (collectionType.IsGenericType && collectionType.GetGenericTypeDefinition() == typeof(ISet<>))
            {
                collectionType = typeof(HashSet<>).MakeGenericType(collectionType.GetGenericArguments());
            }
            Value = Activator.CreateInstance(collectionType, nonPublic: true);
            _collectionTypeInfo = collectionTypeInfo;
            _collectionElementTypeInfo = collectionTypeInfo.EnumerableElementTypeInfo;
        }

        public object Value { get; }

        public void SetCurrentPropertyName(string value) => _propertyName = value;
        
        public JsonTypeInfo CurrentElementTypeInfo => _collectionElementTypeInfo;

        public void SetCurrentElementValue(object value)
        {
            var parameters = string.IsNullOrEmpty(_propertyName) ?
                new[] { value } :
                new[] { _propertyName, value };
            _collectionTypeInfo.CollectionAddMethod.Invoke(Value, parameters);
        }
    }
}
