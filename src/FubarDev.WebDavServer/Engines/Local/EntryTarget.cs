// <copyright file="EntryTarget.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.Props;
using FubarDev.WebDavServer.Props.Dead;
using FubarDev.WebDavServer.Props.Live;



namespace FubarDev.WebDavServer.Engines.Local
{
    /// <summary>
    /// The base class of all existing targets
    /// </summary>
    public abstract class EntryTarget : IExistingTarget
    {

        private readonly IEntry _entry;

        /// <summary>
        /// Initializes a new instance of the <see cref="EntryTarget"/> class.
        /// </summary>
        /// <param name="targetActions">The target actions implementation to use</param>
        /// <param name="parent">The parent collection</param>
        /// <param name="destinationUrl">The destination URL for this entry</param>
        /// <param name="entry">The underlying entry</param>
        protected EntryTarget(
            ITargetActions<CollectionTarget, DocumentTarget, MissingTarget> targetActions,
             CollectionTarget parent,
             Uri destinationUrl,
             IEntry entry)
        {
            TargetActions = targetActions;
            _entry = entry;
            Name = entry.Name;
            Parent = parent;
            DestinationUrl = destinationUrl;
        }

        /// <inheritdoc />
        public string Name { get; }

        /// <summary>
        /// Gets the parent collection target
        /// </summary>

        public CollectionTarget Parent { get; }

        /// <inheritdoc />
        public Uri DestinationUrl { get; }

        /// <summary>
        /// Gets the target actions implementation to use
        /// </summary>

        protected ITargetActions<CollectionTarget, DocumentTarget, MissingTarget> TargetActions { get; }

        /// <inheritdoc />

        public async Task<IReadOnlyCollection<XName>> SetPropertiesAsync(IEnumerable<IUntypedWriteableProperty> properties, CancellationToken cancellationToken)
        {
            List<ILiveProperty> liveProperties = [];
            List<IDeadProperty> deadProperties = [];
            foreach (IUntypedWriteableProperty property in properties)
            {
                if (property is ILiveProperty liveProp)
                {
                    liveProperties.Add(liveProp);
                }
                else
                {
                    IDeadProperty deadProp = (IDeadProperty)property;
                    deadProperties.Add(deadProp);
                }
            }

            IReadOnlyCollection<XName> livePropertiesResult = await SetPropertiesAsync(liveProperties, cancellationToken).ConfigureAwait(false);

            if (deadProperties.Count != 0)
            {
                await SetPropertiesAsync(deadProperties, cancellationToken).ConfigureAwait(false);
            }

            return livePropertiesResult;
        }


        private async Task SetPropertiesAsync(IEnumerable<IDeadProperty> properties, CancellationToken cancellationToken)
        {
            Props.Store.IPropertyStore propertyStore = _entry.FileSystem.PropertyStore;
            if (propertyStore == null)
            {
                return;
            }

            List<XElement> elements = [];
            foreach (IDeadProperty property in properties)
            {
                XElement propValue = await property.GetXmlValueAsync(cancellationToken).ConfigureAwait(false);
                if (!property.IsDefaultValue(propValue))
                {
                    elements.Add(propValue);
                }
            }

            await propertyStore.SetAsync(_entry, elements, cancellationToken).ConfigureAwait(false);
        }



        private async Task<IReadOnlyCollection<XName>> SetPropertiesAsync(IEnumerable<ILiveProperty> properties, CancellationToken cancellationToken)
        {
            Dictionary<XName, bool> isPropUsed = [];
            Dictionary<XName, XElement> propNameToValue = [];
            foreach (ILiveProperty property in properties)
            {
                XName key = property.Name;
                propNameToValue[key] = await property.GetXmlValueAsync(cancellationToken).ConfigureAwait(false);
                isPropUsed[key] = false;
            }

            if (propNameToValue.Count == 0)
            {
                return new XName[0];
            }

            await using (IAsyncEnumerator<IUntypedReadableProperty> propEnum = _entry.GetProperties(TargetActions.Dispatcher, returnInvalidProperties: true).GetAsyncEnumerator(cancellationToken))
            {
                while (await propEnum.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                {
                    XName key = propEnum.Current.Name;
                    isPropUsed[key] = true;
                    if (propEnum.Current is IUntypedWriteableProperty prop && propNameToValue.TryGetValue(key, out XElement propValue))
                    {
                        await prop.SetXmlValueAsync(propValue, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            bool hasUnsetLiveProperties = isPropUsed.Any(x => !x.Value);
            if (hasUnsetLiveProperties)
            {
                List<XName> unsetPropNames = isPropUsed.Where(x => !x.Value).Select(x => x.Key).ToList();
                return unsetPropNames;
            }

            return new XName[0];
        }
    }
}
