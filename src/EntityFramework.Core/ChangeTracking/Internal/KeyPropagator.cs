// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Linq;
using JetBrains.Annotations;
using Microsoft.Data.Entity.Metadata;
using Microsoft.Data.Entity.ValueGeneration;

namespace Microsoft.Data.Entity.ChangeTracking.Internal
{
    public class KeyPropagator
    {
        private readonly IClrAccessorSource<IClrPropertyGetter> _getterSource;
        private readonly IClrCollectionAccessorSource _collectionAccessorSource;
        private readonly IValueGeneratorSelector _valueGeneratorSelector;

        /// <summary>
        ///     This constructor is intended only for use when creating test doubles that will override members
        ///     with mocked or faked behavior. Use of this constructor for other purposes may result in unexpected
        ///     behavior including but not limited to throwing <see cref="NullReferenceException" />.
        /// </summary>
        protected KeyPropagator()
        {
        }

        public KeyPropagator(
            [NotNull] IClrAccessorSource<IClrPropertyGetter> getterSource,
            [NotNull] IClrCollectionAccessorSource collectionAccessorSource,
            [NotNull] IValueGeneratorSelector valueGeneratorSelector)
        {
            _getterSource = getterSource;
            _collectionAccessorSource = collectionAccessorSource;
            _valueGeneratorSelector = valueGeneratorSelector;
        }

        public virtual void PropagateValue([NotNull] InternalEntityEntry entry, [NotNull] IProperty property)
        {
            Debug.Assert(property.IsForeignKey());

            if (!TryPropagateValue(entry, property)
                && property.IsKey())
            {
                var valueGenerator = TryGetValueGenerator(property);

                if (valueGenerator != null)
                {
                    entry[property] = valueGenerator.Next();
                }
            }
        }

        private bool TryPropagateValue(InternalEntityEntry entry, IProperty property)
        {
            var entityType = property.EntityType;
            var stateManager = entry.StateManager;

            foreach (var foreignKey in entityType.ForeignKeys)
            {
                for (var propertyIndex = 0; propertyIndex < foreignKey.Properties.Count; propertyIndex++)
                {
                    if (property == foreignKey.Properties[propertyIndex])
                    {
                        object valueToPropagte = null;

                        foreach (var navigation in entityType.Navigations
                            .Concat(foreignKey.ReferencedEntityType.Navigations)
                            .Where(n => n.ForeignKey == foreignKey)
                            .Distinct())
                        {
                            var principal = TryFindPrincipal(stateManager, navigation, entry.Entity);

                            if (principal != null)
                            {
                                var principalEntry = stateManager.GetOrCreateEntry(principal);
                                var principalProperty = foreignKey.ReferencedProperties[propertyIndex];

                                var principalValue = principalEntry[principalProperty];
                                if (!principalProperty.IsSentinelValue(principalValue))
                                {
                                    valueToPropagte = principalValue;
                                    break;
                                }
                            }
                        }

                        if (valueToPropagte != null)
                        {
                            entry[property] = valueToPropagte;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private ValueGenerator TryGetValueGenerator(IProperty property)
        {
            foreach (var foreignKey in property.EntityType.ForeignKeys)
            {
                for (var propertyIndex = 0; propertyIndex < foreignKey.Properties.Count; propertyIndex++)
                {
                    if (property == foreignKey.Properties[propertyIndex]
                        && property.IsKey())
                    {
                        var generationProperty = foreignKey.GetRootPrincipals(propertyIndex).FirstOrDefault(p => p.GenerateValueOnAdd);

                        if (generationProperty != null)
                        {
                            return _valueGeneratorSelector.Select(generationProperty);
                        }
                    }
                }
            }
            return null;
        }

        private object TryFindPrincipal(StateManager stateManager, INavigation navigation, object dependentEntity)
        {
            if (navigation.PointsToPrincipal)
            {
                return _getterSource.GetAccessor(navigation).GetClrValue(dependentEntity);
            }

            // TODO: Perf
            foreach (var principalEntry in stateManager.Entries
                .Where(e => e.EntityType == navigation.ForeignKey.ReferencedEntityType))
            {
                if (navigation.IsCollection())
                {
                    if (_collectionAccessorSource.GetAccessor(navigation).Contains(principalEntry.Entity, dependentEntity))
                    {
                        return principalEntry.Entity;
                    }
                }
                else if (_getterSource.GetAccessor(navigation).GetClrValue(principalEntry.Entity) == dependentEntity)
                {
                    return principalEntry.Entity;
                }
            }

            return null;
        }
    }
}
