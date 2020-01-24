﻿using System;
using System.Collections.Generic;
using System.Linq;
using Lithnet.GoogleApps.ManagedObjects;
using Lithnet.Logging;
using Lithnet.MetadirectoryServices;
using Microsoft.MetadirectoryServices;

namespace Lithnet.GoogleApps.MA
{
    internal class ApiInterfaceUserAliases : IApiInterface
    {
        private IManagementAgentParameters config;

        public string Api => "useraliases";

        public ApiInterfaceUserAliases(IManagementAgentParameters config)
        {
            this.config = config;
        }

        public void ApplyChanges(CSEntryChange csentry, CSEntryChange committedChanges, SchemaType type, ref object target, bool patch = false)
        {
            User user = (User)target;
            AttributeChange change = this.ApplyUserAliasChanges(csentry, user);

            if (change != null)
            {
                committedChanges.AttributeChanges.Add(change);
            }
        }

        public IEnumerable<AttributeChange> GetChanges(string dn, ObjectModificationType modType, SchemaType type, object source)
        {
            foreach (IAttributeAdapter typeDef in ManagementAgent.Schema[SchemaConstants.User].AttributeAdapters.Where(t => t.Api == this.Api))
            {
                foreach (string attributeName in typeDef.MmsAttributeNames)
                {
                    if (type.HasAttribute(attributeName))
                    {
                        foreach (AttributeChange change in typeDef.CreateAttributeChanges(dn, modType, source))
                        {
                            yield return change;
                        }
                    }
                }
            }
        }

        private void GetUserAliasChanges(CSEntryChange csentry, out IList<string> aliasAdds, out IList<string> aliasDeletes, out bool deletingAll)
        {
            aliasAdds = new List<string>();
            aliasDeletes = new List<string>();
            AttributeChange change = csentry.AttributeChanges.FirstOrDefault(t => t.Name == "aliases");
            deletingAll = false;

            if (csentry.ObjectModificationType == ObjectModificationType.Replace)
            {
                if (change != null)
                {
                    aliasAdds = change.GetValueAdds<string>();
                }

                foreach (string alias in this.config.UsersService.GetAliases(csentry.DN).Except(aliasAdds))
                {
                    aliasDeletes.Add(alias);
                }
            }
            else
            {
                if (change == null)
                {
                    return;
                }

                switch (change.ModificationType)
                {
                    case AttributeModificationType.Add:
                        aliasAdds = change.GetValueAdds<string>();
                        break;

                    case AttributeModificationType.Delete:
                        foreach (string alias in this.config.UsersService.GetAliases(csentry.DN))
                        {
                            aliasDeletes.Add(alias);
                        }

                        deletingAll = true;
                        break;

                    case AttributeModificationType.Replace:
                        aliasAdds = change.GetValueAdds<string>();
                        foreach (string alias in this.config.UsersService.GetAliases(csentry.DN).Except(aliasAdds))
                        {
                            aliasDeletes.Add(alias);
                        }
                        break;

                    case AttributeModificationType.Update:
                        aliasAdds = change.GetValueAdds<string>();
                        aliasDeletes = change.GetValueDeletes<string>();
                        break;

                    case AttributeModificationType.Unconfigured:
                    default:
                        throw new InvalidOperationException("Unknown or unsupported modification type");
                }
            }
        }

        private AttributeChange ApplyUserAliasChanges(CSEntryChange csentry, User user)
        {
            this.GetUserAliasChanges(csentry, out IList<string> aliasAdds, out IList<string> aliasDeletes, out bool deletingAll);

            if (aliasAdds.Count == 0 && aliasDeletes.Count == 0)
            {
                return null;
            }

            AttributeChange change = null;
            IList<ValueChange> valueChanges = new List<ValueChange>();

            try
            {
                if (aliasDeletes != null)
                {
                    foreach (string alias in aliasDeletes)
                    {
                        if (user.PrimaryEmail == null || !user.PrimaryEmail.Equals(alias, StringComparison.CurrentCultureIgnoreCase))
                        {
                            Logger.WriteLine($"Removing alias {alias}", LogLevel.Debug);

                            try
                            {
                                this.config.UsersService.RemoveAlias(csentry.DN, alias);
                            }
                            catch (Google.GoogleApiException ex)
                            {
                                if (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound || 
                                    ex.Error?.Message == "Invalid Input: resource_id")
                                {
                                    Logger.WriteLine($"Alias {alias} does not exist on object");
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }

                        valueChanges.Add(ValueChange.CreateValueDelete(alias));
                    }
                }

                foreach (string alias in aliasAdds)
                {
                    if (!csentry.DN.Equals(alias, StringComparison.CurrentCultureIgnoreCase))
                    {
                        Logger.WriteLine($"Adding alias {alias}", LogLevel.Debug);
                        this.config.UsersService.AddAlias(csentry.DN, alias);
                    }

                    valueChanges.Add(ValueChange.CreateValueAdd(alias));
                }
            }
            finally
            {
                if (valueChanges.Count > 0)
                {
                    if (csentry.ObjectModificationType == ObjectModificationType.Update)
                    {
                        if (deletingAll && valueChanges.Count == aliasDeletes?.Count)
                        {
                            change = AttributeChange.CreateAttributeDelete("aliases");
                        }
                        else
                        {
                            change = AttributeChange.CreateAttributeUpdate("aliases", valueChanges);
                        }
                    }
                    else
                    {
                        change = AttributeChange.CreateAttributeAdd("aliases", valueChanges.Where(u => u.ModificationType == ValueModificationType.Add).Select(t => t.Value).ToList());
                    }
                }
            }

            return change;
        }
    }
}