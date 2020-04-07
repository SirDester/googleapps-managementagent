﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using Lithnet.MetadirectoryServices;
using Microsoft.MetadirectoryServices;

namespace Lithnet.GoogleApps.MA
{
    internal class AdapterNestedType : IAttributeAdapter
    {
        private PropertyInfo propInfo;

        private IList<AdapterPropertyValue> attributes;

        public IEnumerable<string> MmsAttributeNames
        {
            get
            {
                return this.AttributeAdapters.Select(t => t.MmsAttributeName);
            }
        }

        public string MmsAttributeNameBase { get; set; }

        public string GoogleApiFieldName { get; set; }

        public string ManagedObjectPropertyName { get; set; }

        public string Api { get; set; }

        public bool SupportsPatch { get; set; }

        public IList<AdapterSubfield> Fields { get; set; }

        public bool IsReadOnly { get; set; }

        public bool IsAnchor => false;

        public IList<AdapterPropertyValue> AttributeAdapters
        {
            get
            {
                if (this.attributes == null)
                {
                    this.attributes = this.GetConstructedAttributes();
                }

                return this.attributes;
            }
        }

        private IList<AdapterPropertyValue> GetConstructedAttributes()
        {
            return this.GetAttributesWithoutType().ToList();
        }

        private IEnumerable<AdapterPropertyValue> GetAttributesWithoutType()
        {
            foreach (AdapterSubfield item in this.Fields)
            {
                yield return new AdapterPropertyValue
                {
                    AttributeType = item.AttributeType,
                    GoogleApiFieldName = item.GoogleApiFieldName,
                    SupportsPatch = this.SupportsPatch,
                    IsMultivalued = item.IsMultivalued,
                    MmsAttributeName = $"{this.MmsAttributeNameBase}_{item.MmsAttributeNameSuffix}",
                    ManagedObjectPropertyName = item.ManagedObjectPropertyName,
                    Operation = item.Operation,
                    ParentFieldName = this.GoogleApiFieldName,
                    NullValueRepresentation = item.NullValueRepresentation,
                    CastForExport = item.CastForExport,
                    CastForImport = item.CastForImport
                };
            }
        }

        public bool UpdateField(CSEntryChange csentry, object obj)
        {
            if (this.IsReadOnly)
            {
                return false;
            }

            bool hasChanged = false;

            IList<Tuple<AttributeChange, AdapterPropertyValue>> changes = this.GetAttributeChanges(csentry).ToList();

            if (changes.Count == 0)
            {
                return false;
            }

            if (this.propInfo == null)
            {
                this.propInfo = obj.GetType().GetProperty(this.ManagedObjectPropertyName);
            }

            object childObject = this.propInfo.GetValue(obj);
            bool created = false;
            if (childObject == null)
            {
                childObject = Activator.CreateInstance(this.propInfo.PropertyType, new object[] { });
                created = true;
            }

            foreach (Tuple<AttributeChange, AdapterPropertyValue> change in changes)
            {
                if (change.Item2.UpdateField(csentry, childObject))
                {
                    hasChanged = true;
                }
            }

            if (hasChanged && created)
            {
                this.propInfo.SetValue(obj, childObject, null);
            }

            return hasChanged;
        }

        public IEnumerable<SchemaAttribute> GetSchemaAttributes()
        {
            foreach (AdapterSubfield field in this.Fields)
            {
                yield return field.GetSchemaAttribute(this.MmsAttributeNameBase);
            }
        }

        public bool CanPatch(KeyedCollection<string, AttributeChange> changes)
        {
            return this.SupportsPatch || !this.MmsAttributeNames.Any(changes.Contains);
        }

        public IEnumerable<string> GetGoogleApiFieldNames(SchemaType type, string api)
        {
            if (api != null && this.Api != api)
            {
                yield break;
            }

            if (this.GoogleApiFieldName == null)
            {
                yield break;
            }

            string childFields = string.Join(",", this.AttributeAdapters.Where(t => t.GoogleApiFieldName != null && type.HasAttribute(t.MmsAttributeName)).Select(t => t.GoogleApiFieldName));

            if (!string.IsNullOrWhiteSpace(childFields))
            {
                yield return $"{this.GoogleApiFieldName}({childFields})";
            }
        }

        private IEnumerable<Tuple<AttributeChange, AdapterPropertyValue>> GetAttributeChanges(CSEntryChange csentry)
        {
            foreach (AdapterPropertyValue attribute in this.AttributeAdapters)
            {
                if (csentry.HasAttributeChange(attribute.MmsAttributeName))
                {
                    yield return new Tuple<AttributeChange, AdapterPropertyValue>(csentry.AttributeChanges[attribute.MmsAttributeName], attribute);
                }
            }
        }

        public IEnumerable<AttributeChange> CreateAttributeChanges(string dn, ObjectModificationType modType, object obj)
        {
            if (this.propInfo == null)
            {
                this.propInfo = obj.GetType().GetProperty(this.ManagedObjectPropertyName);
            }

            object value = this.propInfo.GetValue(obj);

            if (value == null)
            {
                yield break;
            }

            foreach (AdapterPropertyValue attribute in this.AttributeAdapters)
            {
                foreach (AttributeChange change in attribute.CreateAttributeChanges(dn, modType, value))
                {
                    yield return change;
                }
            }
        }
    }
}
