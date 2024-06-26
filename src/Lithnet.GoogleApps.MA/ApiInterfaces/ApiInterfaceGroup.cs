﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lithnet.Logging;
using Lithnet.MetadirectoryServices;
using Microsoft.MetadirectoryServices;
using Group = Google.Apis.Admin.Directory.directory_v1.Data.Group;
using MmsSchema = Microsoft.MetadirectoryServices.Schema;

namespace Lithnet.GoogleApps.MA
{
    internal class ApiInterfaceGroup : IApiInterfaceObject
    {
        private ApiInterfaceKeyedCollection internalInterfaces;

        private IManagementAgentParameters config;

        protected MASchemaType SchemaType { get; set; }

        public ApiInterfaceGroup(MASchemaType type, IManagementAgentParameters config)
        {
            this.SchemaType = type;
            this.config = config;

            this.internalInterfaces = new ApiInterfaceKeyedCollection
            {
                new ApiInterfaceGroupAliases(config),
                new ApiInterfaceGroupMembership(config),
                new ApiInterfaceGroupSettings(config)
            };
        }

        public string Api => "group";

        public ObjectModificationType DeltaUpdateType => ObjectModificationType.Update;

        public object CreateInstance(CSEntryChange csentry)
        {
            GoogleGroup g = new GoogleGroup();
            g.Group.Email = csentry.DN;
            return g;
        }

        public object GetInstance(CSEntryChange csentry)
        {
            return new GoogleGroup()
            {
                Group = this.config.GroupsService.Get(csentry.GetAnchorValueOrDefault<string>("id") ?? csentry.DN)
            };
        }

        public void DeleteInstance(CSEntryChange csentry)
        {
            this.config.GroupsService.Delete(csentry.GetAnchorValueOrDefault<string>("id") ?? csentry.DN);
        }

        public void ApplyChanges(CSEntryChange csentry, CSEntryChange committedChanges, SchemaType type, ref object target, bool patch = false)
        {
            bool hasChanged = false;

            if (!(target is GoogleGroup group))
            {
                throw new InvalidOperationException();
            }

            hasChanged |= this.SetDNValue(csentry, group);

            foreach (IAttributeAdapter typeDef in this.SchemaType.AttributeAdapters.Where(t => t.Api == this.Api))
            {
                hasChanged |= typeDef.UpdateField(csentry, group.Group);
            }

            if (csentry.ObjectModificationType == ObjectModificationType.Add)
            {
                group.Group = this.config.GroupsService.Add(group.Group);
                committedChanges.ObjectModificationType = ObjectModificationType.Add;
                committedChanges.DN = this.GetDNValue(group);

                Thread.Sleep(1000); // Group membership operations fail on newly created groups if processed too quickly
            }

            if (csentry.IsUpdateOrReplace() && hasChanged)
            {
                string id = csentry.GetAnchorValueOrDefault<string>("id");

                if (patch)
                {
                    group.Group = this.config.GroupsService.Patch(id, group.Group);
                }
                else
                {
                    group.Group = this.config.GroupsService.Update(id, group.Group);
                }
            }

            if (csentry.IsUpdateOrReplace())
            {
                committedChanges.ObjectModificationType = this.DeltaUpdateType;
                committedChanges.DN = this.GetDNValue(group);
            }

            foreach (AttributeChange change in this.GetLocalChanges(csentry.DN, csentry.ObjectModificationType, type, group))
            {
                committedChanges.AttributeChanges.Add(change);
            }

            target = group;

            foreach (IApiInterface i in this.internalInterfaces)
            {
                i.ApplyChanges(csentry, committedChanges, type, ref target, patch);
            }
        }

        public IEnumerable<AttributeChange> GetChanges(string dn, ObjectModificationType modType, SchemaType type, object source)
        {
            foreach (AttributeChange change in this.GetLocalChanges(dn, modType, type, source))
            {
                yield return change;
            }

            foreach (IApiInterface i in this.internalInterfaces)
            {
                foreach (AttributeChange change in i.GetChanges(dn, modType, type, source))
                {
                    yield return change;
                }
            }
        }

        private IEnumerable<AttributeChange> GetLocalChanges(string dn, ObjectModificationType modType, SchemaType type, object source)
        {
            if (!(source is GoogleGroup googleGroup))
            {
                throw new InvalidOperationException();
            }

            foreach (IAttributeAdapter typeDef in this.SchemaType.AttributeAdapters.Where(t => t.Api == this.Api))
            {
                if (typeDef.IsAnchor)
                {
                    continue;
                }

                foreach (AttributeChange change in typeDef.CreateAttributeChanges(dn, modType, googleGroup.Group))
                {
                    if (type.HasAttribute(change.Name))
                    {
                        yield return change;
                    }
                }
            }
        }

        public string GetAnchorValue(string name, object target)
        {
            Group group;

            if (target is GoogleGroup googleGroup)
            {
                group = googleGroup.Group;
            }
            else
            {
                group = target as Group;
            }

            if (group == null)
            {
                throw new InvalidOperationException();
            }

            return group.Id;
        }

        public string GetDNValue(object target)
        {
            Group group;

            if (target is GoogleGroup googleGroup)
            {
                group = googleGroup.Group;
            }
            else
            {
                group = target as Group;
            }

            if (group == null)
            {
                throw new InvalidOperationException();
            }

            return group.Email;
        }

        public Task GetObjectImportTask(MmsSchema schema, BlockingCollection<object> collection, CancellationToken cancellationToken)
        {
            HashSet<string> groupFieldList = new HashSet<string>
            {
                SchemaConstants.Email,
                SchemaConstants.ID
            };

            foreach (string fieldName in ManagementAgent.Schema[SchemaConstants.Group].GetGoogleApiFieldNames(schema.Types[SchemaConstants.Group], "group"))
            {
                groupFieldList.Add(fieldName);
            }

            foreach (string fieldName in ManagementAgent.Schema[SchemaConstants.Group].GetGoogleApiFieldNames(schema.Types[SchemaConstants.Group], "groupaliases"))
            {
                groupFieldList.Add(fieldName);
            }

            string groupFields = string.Format("groups({0}), nextPageToken", string.Join(",", groupFieldList));

            HashSet<string> groupSettingList = new HashSet<string>();

            foreach (string fieldName in ManagementAgent.Schema[SchemaConstants.Group].GetGoogleApiFieldNames(schema.Types[SchemaConstants.Group], "groupsettings"))
            {
                groupSettingList.Add(fieldName);
            }

            bool settingsRequired = groupSettingList.Count > 0;

            string groupSettingsFields = string.Join(",", groupSettingList);

            bool membersRequired =
                ManagementAgent.Schema[SchemaConstants.Group].AttributeAdapters.Where(u => u.Api == "groupmembership").Any(v =>
                {
                    return v.MmsAttributeNames.Any(attributeName => schema.Types[SchemaConstants.Group].Attributes.Contains(attributeName));
                });

            Task t = new Task(() =>
            {
                Logger.WriteLine("Requesting group fields: " + groupFields);
                Logger.WriteLine("Requesting group settings fields: " + groupSettingsFields);

                Logger.WriteLine("Requesting settings: " + settingsRequired);
                Logger.WriteLine("Requesting members: " + membersRequired);

                Regex filter = null;

                if (!string.IsNullOrEmpty(this.config.GroupRegexFilter))
                {
                    filter = new Regex(this.config.GroupRegexFilter);
                    Logger.WriteLine("Regex filter: " + this.config.GroupRegexFilter);
                }
                else
                {
                    Logger.WriteLine("Regex filter: <none>");
                }

                foreach (GoogleGroup group in this.config.GroupsService.GetGroups(this.config.CustomerID, membersRequired, settingsRequired, groupFields, MAConfigurationSection.Configuration.GroupSettingsApi.ImportThreadsGroupSettings, MAConfigurationSection.Configuration.DirectoryApi.ImportThreadsGroupMember, this.config.ExcludeUserCreated, filter))
                {
                    collection.Add(this.GetCSEntryForGroup(group, schema));
                    Debug.WriteLine($"Created CSEntryChange for group: {group.Group.Email}");

                    continue;
                }
            }, cancellationToken);

            t.Start();

            return t;
        }

        private CSEntryChange GetCSEntryForGroup(GoogleGroup group, Schema schema)
        {
            CSEntryChange csentry;

            if (group.Errors.Count > 0)
            {
                csentry = CSEntryChange.Create();
                csentry.ObjectType = "group";
                csentry.ObjectModificationType = ObjectModificationType.Add;
                csentry.DN = group.Group.Email;
                csentry.ErrorCodeImport = MAImportError.ImportErrorCustomContinueRun;
                csentry.ErrorDetail = group.Errors.FirstOrDefault()?.StackTrace;
                csentry.ErrorName = group.Errors.FirstOrDefault()?.Message;
            }
            else
            {
                csentry = ImportProcessor.GetCSEntryChange(group, schema.Types[SchemaConstants.Group], this.config);
            }

            return csentry;
        }

        private bool SetDNValue(CSEntryChange csentry, GoogleGroup e)
        {
            if (csentry.ObjectModificationType != ObjectModificationType.Replace && csentry.ObjectModificationType != ObjectModificationType.Update)
            {
                return false;
            }

            string newDN = csentry.GetNewDNOrDefault<string>();

            if (newDN == null)
            {
                return false;
            }

            e.Group.Email = newDN;

            return true;
        }
    }
}

