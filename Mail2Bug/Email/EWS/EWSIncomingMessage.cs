﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using log4net;
using Microsoft.Exchange.WebServices.Data;

namespace Mail2Bug.Email.EWS
{
    public class EWSIncomingMessage : IIncomingEmailMessage
    {
        private const int PidTagBodyHtmlTag = 0x1013;
        private const int PidTagConversationIdTag = 0x3013;

        private readonly EmailMessage _message;
        private readonly byte[] _conversationId;
        private readonly bool _useConversationGuidOnly;

        // Extended property for PidTagBodyHtmlTag, which is the HTML body of the Message object
        // See https://msdn.microsoft.com/en-us/library/ee202050(v=exchg.80).aspx
        private static readonly ExtendedPropertyDefinition PidTagBodyHtml = new ExtendedPropertyDefinition(PidTagBodyHtmlTag, MapiPropertyType.Binary);

        public EWSIncomingMessage(EmailMessage message, bool useConversationGuidOnly = false)
        {
            _message = message;
            _useConversationGuidOnly = useConversationGuidOnly;

            // Extended property for PidTagConversationId, which is the GUID portion of the ConversationIndex
            // See https://msdn.microsoft.com/en-us/library/cc433490(v=EXCHG.80).aspx and
            // https://msdn.microsoft.com/en-us/library/ee204279(v=exchg.80).aspx for more information
            ExtendedPropertyDefinition conversationId = new ExtendedPropertyDefinition(PidTagConversationIdTag, MapiPropertyType.Binary);

            message.Load(new PropertySet(
                    ItemSchema.Subject,
                    ItemSchema.Body, 
                    PidTagBodyHtml,
                    EmailMessageSchema.ConversationIndex, 
                    conversationId,
                    EmailMessageSchema.Sender,
                    EmailMessageSchema.From,
                    EmailMessageSchema.ToRecipients,
                    EmailMessageSchema.CcRecipients,
                    ItemSchema.MimeContent,
                    ItemSchema.DateTimeReceived,
                    ItemSchema.DateTimeSent,
                    EmailMessageSchema.ConversationTopic,
                    ItemSchema.Attachments,
                    ItemSchema.HasAttachments,
                    MeetingRequestSchema.Location,
                    MeetingRequestSchema.Start,
                    MeetingRequestSchema.End
                ) { RequestedBodyType = BodyType.Text });
            
            message.TryGetProperty(conversationId, out _conversationId);

            Attachments = BuildAttachmentList(message);
        }

        public string Subject { get { return _message.Subject; } }
        public string ConversationTopic { get { return _message.ConversationTopic; } }

        public string RawBody
        {
            get
            {
                byte[] bodyHtml;
                if (_message.TryGetProperty(PidTagBodyHtml, out bodyHtml))
                {
                    return Encoding.Default.GetString(bodyHtml);
                }

                return _message.Body.Text ?? string.Empty;
            }
        }
        public string PlainTextBody { get { return GetPlainTextBody(_message); } }

        public string ConversationId
        {
            get
            {
                return _useConversationGuidOnly ? GetConversationGuid() : GetConversationIndex();
            }
        }

        public string SenderName { get { return _message.Sender.Name; } }
        public string SenderAlias { get { return GetAliasFromEmailAddress(_message.Sender.Address); } }
        public string SenderAddress { get { return _message.Sender.Address; } }
        public IEnumerable<string> ToAddresses { get { return _message.ToRecipients.Select(x => x.Address); } }
        public IEnumerable<string> CcAddresses { get { return _message.CcRecipients.Select(x => x.Address); } }
        public IEnumerable<string> ToNames { get { return _message.ToRecipients.Select(x => x.Name); } }
        public IEnumerable<string> CcNames { get { return _message.CcRecipients.Select(x => x.Name); } }
        public DateTime SentOn { get { return _message.DateTimeSent; } }
        public DateTime ReceivedOn { get { return _message.DateTimeReceived; } }
        public bool IsHtmlBody { get { return _message.Body.BodyType == BodyType.HTML; } }

        public string Location
        {
            get
            {
                var x = _message as MeetingRequest;
                return (x == null) ? "Not a meeting request - no location" : x.Location;
            }
        }

        public DateTime? StartTime
        {
            get
            {
                var x = _message as MeetingRequest;
                return (x == null) ? null : (DateTime?)x.Start;
            }
        }

        public DateTime? EndTime
        {
            get
            {
                var x = _message as MeetingRequest;
                return (x == null) ? null : (DateTime?)x.End;
            }
        }

        public string SaveToFile()
        {
            return SaveToFile(Path.Combine(Path.GetTempPath(), "OriginalMessage.eml"));
        }

        public string SaveToFile(string filename)
        {
            using (var fs = new FileStream(filename, FileMode.Create, FileAccess.Write))
            {
                var contents = _message.MimeContent.Content;
                Logger.DebugFormat("Message '{0}' is {1} bytes long", _message.Subject, contents.Length);
                fs.Write(contents, 0, contents.Length);
            }

            return filename;
        }

        public string GetLastMessageText()
        {
            return EmailBodyProcessingUtils.GetLastMessageText(this);
        }

        public void Delete()
        {
            _message.Delete(DeleteMode.MoveToDeletedItems);
        }

        public IEnumerable<IIncomingEmailAttachment> Attachments { get; private set; }

        /// <summary>
        /// If the extended property for the conversation index is null, fall back to taking
        /// the ConversationID from the ConversationIndex directly, which is 16 bytes, starting at byte 6.
        /// See https://msdn.microsoft.com/en-us/library/ee202481(v=exchg.80).aspx for more information.
        /// </summary>
        /// <returns></returns>
        public string GetConversationGuid()
        {
            return _conversationId == null
               ? this.ConversationId.Substring(12, 32)
               : string.Join("", _conversationId.Select(b => b.ToString("X2")));
        }

        public string GetConversationIndex()
        {
            return string.Join("", _message.ConversationIndex.Select(b => b.ToString("X2")));
        }

        /// <summary>
        /// Send a reply over the original email.
        /// Reply format is HTML
        /// </summary>
        /// <param name="replyHtml">HTML of the contents</param>
        /// <param name="replyAll">true = Reply All; false = reply only to sender</param>
        public void Reply(string replyHtml, bool replyAll)
        {
            var reply = _message.CreateReply(replyAll);
            reply.BodyPrefix = new MessageBody(BodyType.HTML, replyHtml);
            reply.Send();
        }

        private static string GetPlainTextBody(Item message)
        {
            // When there's no text whatsoever in the email, EWS may return null rather than an empty string. We normalize
            // to empty string to avoid repeated null checks later on, since empty string represents the same meaning as null
            // in our context.
            var text = message.Body.Text ?? string.Empty;
            return message.Body.BodyType == BodyType.Text ? text : EmailBodyProcessingUtils.ConvertHtmlMessageToPlainText(text);
        }

        private static IEnumerable<IIncomingEmailAttachment> BuildAttachmentList(Item message)
        {
            var attachmentList = new List<IIncomingEmailAttachment>();
            foreach (var attachment in message.Attachments)
            {
                HandleSingleAttachment(attachment, attachmentList);
            }
            return attachmentList;
        }

        private static void HandleSingleAttachment(Attachment attachment, ICollection<IIncomingEmailAttachment> attachmentList)
        {
            if (attachment is FileAttachment)
            {
                Logger.DebugFormat("Loading file attachment");
                attachmentList.Add(new EWSIncomingFileAttachment(attachment as FileAttachment));
                return;
            }
            if (attachment is ItemAttachment)
            {
                Logger.DebugFormat("Loading item attachment");
                attachmentList.Add(new EWSIncomingItemAttachment(attachment as ItemAttachment));
                return;
            }

            Logger.ErrorFormat("Skipping attachment because it's not a file attachment ({0})", attachment.Name);
        }

        private static string GetAliasFromEmailAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                Logger.ErrorFormat("GetAliasFromEmailAddress: Can't get alias from empty address");
                throw new ArgumentException("Can't extract alias from empty address", "address");
            }

            Logger.DebugFormat("address={0}",address);
            var aliasFromEmailAddress = address.Substring(0, address.IndexOf('@'));

            return string.IsNullOrEmpty(aliasFromEmailAddress) ? address : aliasFromEmailAddress;
        }

        public Item MoveMessage(FolderId destination)
        {
            return _message.Move(destination);
        }

        private static readonly ILog Logger = LogManager.GetLogger(typeof(EWSIncomingMessage));
    }
}
