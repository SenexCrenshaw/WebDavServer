// <copyright file="EntryProperties.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.Props.Dead;
using FubarDev.WebDavServer.Props.Live;
using FubarDev.WebDavServer.Props.Store;



namespace FubarDev.WebDavServer.Props
{
    /// <summary>
    /// The asynchronously enumerable properties for a <see cref="IEntry"/>
    /// </summary>
    public class EntryProperties : IAsyncEnumerable<IUntypedReadableProperty>
    {

        private readonly IEntry _entry;



        private readonly IEnumerable<IUntypedReadableProperty> _predefinedProperties;


        private readonly IPropertyStore _propertyStore;

        private readonly int? _maxCost;

        private readonly bool _returnInvalidProperties;

        /// <summary>
        /// Initializes a new instance of the <see cref="EntryProperties"/> class.
        /// </summary>
        /// <param name="entry">The entry whose properties are to enumerate</param>
        /// <param name="predefinedProperties">The predefined properties for the entry</param>
        /// <param name="propertyStore">The property store to get the remaining dead properties for</param>
        /// <param name="maxCost">The maximum cost of the properties to return</param>
        /// <param name="returnInvalidProperties">Do we want to get invalid live properties?</param>
        public EntryProperties(
             IEntry entry,
              IEnumerable<IUntypedReadableProperty> predefinedProperties,
             IPropertyStore propertyStore,
            int? maxCost,
            bool returnInvalidProperties)
        {
            _entry = entry;
            _predefinedProperties = predefinedProperties;
            _propertyStore = propertyStore;
            _maxCost = maxCost;
            _returnInvalidProperties = returnInvalidProperties;
        }

        /// <inheritdoc />
        public IAsyncEnumerator<IUntypedReadableProperty> GetAsyncEnumerator(CancellationToken cancellationToken)
        {
            return new PropertiesEnumerator(_entry, _predefinedProperties, _propertyStore, _maxCost, _returnInvalidProperties);
        }

        private class PropertiesEnumerator : IAsyncEnumerator<IUntypedReadableProperty>
        {

            private readonly IEntry _entry;


            private readonly IPropertyStore _propertyStore;

            private readonly int? _maxCost;

            private readonly bool _returnInvalidProperties;


            private readonly IEnumerator<IUntypedReadableProperty> _predefinedPropertiesEnumerator;

            private readonly Dictionary<XName, IUntypedReadableProperty> _emittedProperties = [];

            private bool _predefinedPropertiesFinished;


            private IEnumerator<IDeadProperty> _deadPropertiesEnumerator;

            public PropertiesEnumerator(
                 IEntry entry,
                  IEnumerable<IUntypedReadableProperty> predefinedProperties,
                 IPropertyStore propertyStore,
                int? maxCost,
                bool returnInvalidProperties)
            {
                _entry = entry;
                _propertyStore = propertyStore;
                _maxCost = maxCost;
                _returnInvalidProperties = returnInvalidProperties;

                HashSet<XName> emittedProperties = [];
                List<IUntypedReadableProperty> predefinedPropertiesList = [];
                foreach (IUntypedReadableProperty property in predefinedProperties)
                {
                    if (emittedProperties.Add(property.Name))
                    {
                        predefinedPropertiesList.Add(property);
                    }
                }

                _predefinedPropertiesEnumerator = predefinedPropertiesList.GetEnumerator();
            }

            public IUntypedReadableProperty Current { get; private set; }

            async ValueTask<bool> IAsyncEnumerator<IUntypedReadableProperty>.MoveNextAsync()
            {
                CancellationToken cancellationToken = CancellationToken.None;
                while (true)
                {
                    IUntypedReadableProperty result = await GetNextPropertyAsync(cancellationToken).ConfigureAwait(false);
                    if (result == null)
                    {
                        Current = null;
                        return false;
                    }

                    if (_emittedProperties.TryGetValue(result.Name, out IUntypedReadableProperty oldProperty))
                    {
                        // Property was already emitted - don't return it again.
                        // The predefined dead properties are reading their values from the property store
                        // themself and don't need to be intialized again.
                        continue;
                    }

                    if (!_returnInvalidProperties)
                    {
                        if (result is ILiveProperty liveProp && !await liveProp.IsValidAsync(cancellationToken).ConfigureAwait(false))
                        {
                            // The properties value is not valid
                            continue;
                        }
                    }

                    _emittedProperties.Add(result.Name, result);
                    Current = result;
                    return true;
                }
            }

            public void Dispose()
            {
                _predefinedPropertiesEnumerator.Dispose();
                _deadPropertiesEnumerator?.Dispose();
            }

            private async Task<IUntypedReadableProperty> GetNextPropertyAsync(CancellationToken cancellationToken)
            {
                if (!_predefinedPropertiesFinished)
                {
                    if (_predefinedPropertiesEnumerator.MoveNext())
                    {
                        return _predefinedPropertiesEnumerator.Current;
                    }

                    _predefinedPropertiesFinished = true;

                    if (_propertyStore == null || (_maxCost.HasValue && _propertyStore.Cost > _maxCost))
                    {
                        return null;
                    }

                    IReadOnlyCollection<IDeadProperty> deadProperties = await _propertyStore.LoadAsync(_entry, cancellationToken).ConfigureAwait(false);
                    _deadPropertiesEnumerator = deadProperties.GetEnumerator();
                }

                if (_propertyStore == null || (_maxCost.HasValue && _propertyStore.Cost > _maxCost))
                {
                    return null;
                }

                Debug.Assert(_deadPropertiesEnumerator != null, "_deadPropertiesEnumerator != null");
                return _deadPropertiesEnumerator == null
                    ? throw new InvalidOperationException("Internal error: The dead properties enumerator was not initialized")
                    : !_deadPropertiesEnumerator.MoveNext() ? null : (IUntypedReadableProperty)_deadPropertiesEnumerator.Current;
            }
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
