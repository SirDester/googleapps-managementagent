﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using Google.Apis.Gmail.v1.Data;
using Lithnet.GoogleApps.ManagedObjects;
using Lithnet.Logging;
using Lithnet.MetadirectoryServices;
using Microsoft.MetadirectoryServices;

namespace Lithnet.GoogleApps.MA
{
    internal class ApiInterfaceUserSendAs : IApiInterface
    {
        private IManagementAgentParameters config;

        public string Api => "usersendas";

        private string attributeName;

        private string typeName;

        public ApiInterfaceUserSendAs(IManagementAgentParameters config, string typeName)
        {
            this.config = config;
            this.attributeName = $"{typeName}_{SchemaConstants.SendAs}";
            this.typeName = typeName;
        }

        public void ApplyChanges(CSEntryChange csentry, CSEntryChange committedChanges, SchemaType type, ref object target, bool patch = false)
        {
            AttributeChange change = this.ApplySendAsChanges(csentry);

            if (change != null)
            {
                committedChanges.AttributeChanges.Add(change);
            }
        }

        public IEnumerable<AttributeChange> GetChanges(string dn, ObjectModificationType modType, SchemaType type, object source)
        {
            if (!type.HasAttribute(this.attributeName))
            {
                yield break;
            }

            IAttributeAdapter typeDef = ManagementAgent.Schema[this.typeName].AttributeAdapters.First(t => t.Api == this.Api);

            IList<string> sendAsAddresses = this.GetNonPrimarySendAsFormattedAddresses(((User)source).PrimaryEmail);

            foreach (AttributeChange change in typeDef.CreateAttributeChanges(dn, modType, new {SendAs = sendAsAddresses}))
            {
                yield return change;
            }
        }

        private IList<SendAs> GetNonPrimarySendAs(string dn)
        {
            return this.config.GmailService.GetSendAs(dn).Where(t => !t.IsPrimary.HasValue || !t.IsPrimary.Value).ToList();
        }

        private IList<string> GetNonPrimarySendAsAddresses(string dn)
        {
            return this.config.GmailService.GetSendAs(dn).Where(t => !t.IsPrimary.HasValue || !t.IsPrimary.Value).Select(t => t.SendAsEmail).ToList();
        }

        private IList<string> GetNonPrimarySendAsFormattedAddresses(string dn)
        {
            return this.config.GmailService.GetSendAs(dn).Where(t => !t.IsPrimary.HasValue || !t.IsPrimary.Value).Select(t => new MailAddress(t.SendAsEmail, t.DisplayName).ToString()).ToList();
        }

        private void GetUserSendAsChanges(CSEntryChange csentry, out IList<string> adds, out IList<string> deletes)
        {
            adds = new List<string>();
            deletes = new List<string>();

            AttributeChange change = csentry.AttributeChanges.FirstOrDefault(t => t.Name == this.attributeName);

            if (csentry.ObjectModificationType == ObjectModificationType.Replace)
            {
                if (change != null)
                {
                    adds = change.GetValueAdds<string>();
                }

                foreach (string sendAsAddress in this.GetNonPrimarySendAsFormattedAddresses(csentry.DN).Except(adds))
                {
                    deletes.Add(sendAsAddress);
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
                        adds = change.GetValueAdds<string>();
                        break;

                    case AttributeModificationType.Delete:
                        foreach (string sendAsAddress in this.GetNonPrimarySendAsFormattedAddresses(csentry.DN))
                        {
                            deletes.Add(sendAsAddress);
                        }
                        break;

                    case AttributeModificationType.Replace:
                        adds = change.GetValueAdds<string>();

                        foreach (string sendAsAddress in this.GetNonPrimarySendAsFormattedAddresses(csentry.DN).Except(adds))
                        {
                            deletes.Add(sendAsAddress);
                        }
                        break;

                    case AttributeModificationType.Update:
                        adds = change.GetValueAdds<string>();
                        deletes = change.GetValueDeletes<string>();
                        break;

                    case AttributeModificationType.Unconfigured:
                    default:
                        throw new InvalidOperationException("Unknown or unsupported modification type");
                }
            }
        }

        private AttributeChange ApplySendAsChanges(CSEntryChange csentry)
        {
            this.GetUserSendAsChanges(csentry, out IList<string> adds, out IList<string> deletes);

            if (adds.Count == 0 && deletes.Count == 0)
            {
                return null;
            }

            AttributeChange change = null;
            IList<ValueChange> valueChanges = new List<ValueChange>();

            try
            {
                if (deletes != null)
                {
                    foreach (string delete in deletes)
                    {
                        Logger.WriteLine($"Removing send as address {delete}");
                        MailAddress address = new MailAddress(delete);
                        this.config.GmailService.RemoveSendAs(csentry.DN, address.Address);
                        valueChanges.Add(ValueChange.CreateValueDelete(delete));
                    }
                }

                foreach (string add in adds)
                {
                    Logger.WriteLine($"Adding send as address {add}");

                    MailAddress address = new MailAddress(add);
                    SendAs sendAs = new SendAs
                    {
                        DisplayName = address.DisplayName,
                        SendAsEmail = address.Address,
                    };

                    if (this.config.MakeNewSendAsAddressesDefault)
                    {
                        sendAs.IsDefault = true;
                    }

                    this.config.GmailService.AddSendAs(csentry.DN, sendAs);
                    valueChanges.Add(ValueChange.CreateValueAdd(add));
                }
            }
            finally
            {
                if (valueChanges.Count > 0)
                {
                    if (csentry.ObjectModificationType == ObjectModificationType.Update)
                    {
                        change = AttributeChange.CreateAttributeUpdate(this.attributeName, valueChanges);
                    }
                    else
                    {
                        change = AttributeChange.CreateAttributeAdd(this.attributeName, valueChanges.Where(u => u.ModificationType == ValueModificationType.Add).Select(t => t.Value).ToList());
                    }
                }
            }

            return change;
        }
    }
}
